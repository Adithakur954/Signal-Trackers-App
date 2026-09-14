# Combined L3/Event summary export

Authenticated backend endpoints:

```text
GET /api/L3Event/GenerateDiagnosticL3SummaryExcel?sessionId=189
GET /api/L3Event/GenerateDiagnosticL3SummaryPdf?sessionId=189
GET /api/L3Event/GetDiagnosticL3Summary?sessionId=189
```

All three routes also exist under `/api/MapView` and enforce the same session/upload access checks.
Export responses are downloadable XLSX/PDF files with a `Content-Disposition` filename. The frontend
must request a blob and download it; an existing browser-generated workbook does not automatically
switch to these endpoints. The frontend source is not in this repository.

The Excel workbook preserves Summary, Call Summary, Technology Summary and Sheet Messages,
and adds L3 Dashboard. Its three tables contain KPI results and observations, mobility counts,
and decoded parameters. The PDF contains the same sections and calculations.

## JSON summary for the frontend

`GetDiagnosticL3Summary` returns `application/json`; use the normal authenticated JSON client,
not a Blob download. It uses exactly the same query, access checks, filters and calculations as
the Excel/PDF exports. Examples:

```text
GET /api/L3Event/GetDiagnosticL3Summary?sessionId=189
GET /api/L3Event/GetDiagnosticL3Summary?sessionIds=185,189
GET /api/L3Event/GetDiagnosticL3Summary?uploadId=185
```

The response has `status: 1` and a `data` object with these fields:

| Field | Contents |
| --- | --- |
| `sourceFile`, `scope`, `generatedAt` | Source label, actual query scope and UTC generation time |
| `hasData`, `totalRows`, `l3Rows`, `eventRows` | Whether any filtered rows exist and the row counts |
| `kpis` | Dashboard KPI rows: `{ parameter, result, observation }` |
| `mobility` | Mobility rows with the same three fields |
| `parameters` | Decoded parameter rows with the same three fields |
| `technologies` | `{ technology, rows, interfaces }` entries |
| `calls` | `{ call, technology, start, end, result, setupTime, duration, reason }` entries; timing strings are in seconds |

Render `data.kpis`, `data.mobility` and `data.parameters` as the three dashboard tables.
Results preserve the report's display strings, including units and `Not available`. The JSON
omits full Sheet Messages to keep the response focused on the summary. Empty selections currently
return `status: 1`, `hasData: false`, zero row counts and the unavailable-value dashboard; the frontend
should show a no-data state. Validation/access/load-limit errors use the shared HTTP error responses.
This endpoint does not change the existing `GetDiagnosticAnalyzerSummary` contract.

## Scope and filters

Supply `sessionId`, comma-separated `sessionIds` (alias `session_ids`), or `uploadId`.
An upload and sessions supplied together are intersected using the existing diagnostic query.
Optional filters, shared by the exports and JSON summary:

| Query | Meaning |
| --- | --- |
| `technology` | Timeline technology label(s), comma-separated, e.g. `5G,LTE` |
| `sources` | `l3`, `event`, or both, comma-separated |
| `direction` | Direction label(s), e.g. `UL,DL` |
| `channel` | Channel label(s), e.g. `PCCH` |
| `interface` | Timeline interface label(s) |
| `search` | Case-insensitive substring in message or detail |
| `failuresOnly` | Restrict to timeline failure/error/critical rows |
| `timeFrom`, `timeTo` | Inclusive time of day, e.g. `12:15:30.000`; a descending interval crosses midnight |
| `sourceFileName` | Display name for the report |
| `take` | Maximum loaded rows per source, default/maximum 50,000 |
| `reportRows` | PDF message-row limit only, default/maximum 100,000 |

For example:

```text
/api/L3Event/GenerateDiagnosticL3SummaryExcel?sessionId=189&sources=l3,event&technology=5G&direction=DL
```

All dashboard and technology counts use the filtered messages. Call outcomes use the full loaded
session window; only calls associated with selected messages appear. Filtering does not fabricate
a new call attempt from a partial window. A requested PDF message-row limit is disclosed in the PDF;
its dashboard still uses all selected rows. Excel includes all selected messages. Long Excel details
continue on additional rows, repeating the message columns; Summary counts source messages.

The server reads one extra row to detect the load limit. If either source exceeds `take`, it returns
422 instead of silently producing a partial dashboard. Increase `take` up to 50,000 or select fewer
sessions. Time/search filters are applied after loading; they cannot bypass this load guard.

## Calculation rules

- Sources are the imported `tbl_l3_log` and `tbl_event_log` records only. Export does not reimport ZIPs
  or read NetworkLog/KPI files. ZIP TXT records that were not imported cannot contribute to the report.
- Direction/channel columns take priority; missing values fall back to captured/decoded detail.
- Reference workbooks supply the layout, never fixed report values.
- Event signal-change statistics use the new value once per event. Unavailable sentinels and
  out-of-range RF observations are excluded. These are event-observation averages, not time-weighted
  or regularly sampled network averages.
- Serving PCI counts exclude neighbour-list PCIs. Negative/unavailable identifiers are excluded.
- A1–A6/B1–B2 counts come from actual L3 measurement-report messages, not event configuration rows.
- Reconfiguration completion ratios compare observed counts, without assuming correlated transactions.
  RACH ratios use explicit exported handover RACH outcomes. Zero observed failures is not proof that
  no failures occurred outside the selected capture.
- Registration observation ratios do not mean registration procedure success. Explicit NR RAT
  evidence alone does not identify SA versus NSA. Technology Summary retains timeline labels,
  including Unknown and any contextual inference from the existing analyzer.
- Unsupported/missing metrics show `Not available`. Explicit named decoded performance fields are
  retained where supported; link-capacity estimates are never treated as throughput.

## Verification

```powershell
dotnet build SignalTracker.csproj --no-restore
dotnet run --project CallAnalyzerRegression/CallAnalyzerRegression.csproj -- --l3-report <zip-path> artifacts/l3-summary
```

The regression checks filtering, unavailable values, neighbour exclusion, configuration/report
distinction, long Unicode details and formula-like text, the five-sheet XLSX structure, PDF sections,
and shared calculation results. With the Sunil sample ZIP it verifies 1,773 imported rows,
76 measurement reports, 19 reconfigurations, 19 completions, 8 handover RACH success events,
and SS-RSRP/RSRQ/SINR averages of -83.2/-12.5/11.3.
