# TRAI Voice Call Parameters Backend TODO

Source file reviewed: `C:\Users\Vinfocom\Downloads\TRAI_Voice_Call_Parameters.xlsx`

The spreadsheet contains these voice-call parameters:

| Parameter | Backend status | Notes / required work |
|---|---|---|
| Call Setup Success Rate (CSSR) | Partial | Backend has call connected/not-connected summaries from L3/Event call analysis, but needs a dedicated TRAI CSSR metric: `successful call setups / total call attempts * 100`, grouped by project/session/operator/technology/date. |
| Call Drop Rate (CDR) | Partial | Backend detects dropped calls in diagnostic summaries, but needs a dedicated CDR metric: `dropped established calls / total established calls * 100`, with drilldown rows and report output. |
| Call Setup Time (CST) | Partial | Backend stores/returns setup time in L3/Event call summaries and sub-session analytics. Need a dedicated TRAI CST aggregate: average/min/max/P95 setup time by filter scope. |
| Voice Quality (MOS) | Supported for NetworkLog; partial for Voice reports | MOS is imported from NetworkLog and used in map/report thresholds. Need voice-call-level MOS aggregation linked to call/sub-session, plus acceptance classification in report output. |
| Handover Success Rate (HSR) | Partial / missing exact KPI | Backend counts observed handover rows and some RACH outcomes, but does not yet correlate handover attempt-success-failure as true HSR. Need correlated HSR engine from L3/Event handover procedures. |
| Silence Call >4s | Missing | Need RTP/audio gap detection or parser support for silence/gap fields from logs/subsessions. Add count of calls with silence duration greater than 4 seconds. |
| Silence Call Rate | Missing | Depends on Silence Call >4s. Metric: `silence calls >4s / total established calls * 100`. Add API/report field and threshold criteria. |

## Backend implementation checklist

1. Add a `VoiceKpiService` that computes TRAI voice KPIs from L3/Event call summaries, sub-session data, and NetworkLog MOS where available.
2. Add API endpoint, for example `GET /api/PostProcessing/voice-kpis`, with filters: project, session_ids, upload_id, operator, technology, date range.
3. Return these fields in one response: CSSR, CDR, CST avg/min/max/P95, MOS avg/min/max, HSR, Silence Call >4s count, Silence Call Rate.
4. Add detail/drilldown endpoint for failed call setup, dropped call, handover failure, and silence call rows.
5. Add report output into PDF/Excel templates.
6. Add threshold/default acceptance criteria entries for these voice KPIs in `report_acceptance_json`.
7. Add regression tests using a small fixture with known call attempts, connected calls, drops, handovers, MOS, and silence gaps.

## Current conclusion

No parameter is completely ignored in the backend roadmap, but these are not yet fully TRAI-grade calculated:

- CSSR
- CDR
- CST aggregate
- HSR exact correlated success rate
- Silence Call >4s
- Silence Call Rate

MOS is already imported and reported, but needs call-level aggregation for a complete voice-call report.

## Backend implementation added

Added post-processing support so TRAI voice KPIs are no longer guessed when the uploaded ZIP/session does not contain the required source data.

### New APIs

```http
GET /api/PostProcessing/capabilities?sessionId={id}
GET /api/PostProcessing/voice-kpis?sessionId={id}
POST /api/PostProcessing/voice-kpis
```

POST body example:

```json
{
  "SessionIds": [8085],
  "UploadId": null,
  "Take": 100000
}
```

### KPI status rules

- `calculated`: required numerator/denominator/source samples are available.
- `partial`: related observations exist, but exact KPI calculation is not safe.
- `unavailable`: required data is missing from the ZIP/session.

### Implemented KPI handling

- CSSR: calculated from `tbl_l3_event_call_summary` when call attempts and connected/dropped calls are available.
- CDR: calculated from `tbl_l3_event_call_summary` when established calls and dropped calls are available.
- CST: calculated from `tbl_l3_event_call_summary.setup_time` when connected call timing is available.
- MOS: calculated from non-empty `tbl_network_log.mos` samples.
- HSR: calculated only when handover success/failure evidence is present; otherwise returns partial/unavailable.
- Silence Call >4s: calculated only when silence/RTP duration evidence greater than 4 seconds is present.
- Silence Call Rate: calculated only when both silence events and total voice call denominator are available.
