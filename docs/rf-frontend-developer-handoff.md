# Frontend developer handoff: RF reports, company email and Wi-Fi

Updated: 27 October 2026. Routes verified against backend source; production deployment is not confirmed by this document.

## Backend and login

The existing frontend `.env.production` currently sets `VITE_CSHARP_API_URL=https://s-traccceer.vinfocom.co.in`. Use that backend origin only after deployment is confirmed. The existing API client in `src/api/apiService.js` already uses this setting and `withCredentials: true`; reuse its login, cookies and CSRF handling.

The RF request examples use `https://s-traccceer.vinfocom.co.in` as the deployed backend origin. Confirm the frontend's resolved `VITE_CSHARP_API_URL` matches it and use that same origin for login and RF calls. Development user-secrets do not configure the deployed server.

Existing login is `POST /api/auth/login` with `email`, `password`, `country_code` (`IN` / `TW`). The successful login's resolved `country` / `source_db` selects the region. Backend derives company/user from verified claims. Do not send a company ID or an RF service key to choose another company/region.

Authenticated POST/DELETE calls need `X-CSRF-TOKEN` matching `st.csrf` when enabled. GET needs login cookies but no CSRF header. All recipient/email-delivery routes below require company admin or super admin (roles 2/3); ordinary users and reporting service keys cannot manage these screens.

## Required behavior

After the app finishes a drive and confirms its final ZIP upload, the backend generates a PDF and emails its attachment to the company's registered `tbl_company.email`. The company mailbox is automatically subscribed during upload registration; do not require a manual recipient registration, Generate PDF or Send Email action for this workflow.

Show company-managed recipients and optional additional recipients. Show PDF generation separately from email delivery. The registered company email is a destination; it is not necessarily the uploader's login email or the backend SMTP sender.

## Company recipients

| Method | Endpoint | Request | Response |
| --- | --- | --- | --- |
| GET | `/api/rf-logs/recipients` | No body | 200: region/companyId/recipients |
| POST | `/api/rf-logs/recipients` | Additional-recipient JSON | 200: region/companyId/recipient |
| DELETE | `/api/rf-logs/recipients/{recipientId}` | No body | 204 for removable subscriptions; 409 for an enabled company default |

GET example:

```json
{
  "region":"IN",
  "companyId":7,
  "recipients":[
    {
      "id":"58e6bdee-b843-4a1f-b031-ef2f8fe34cb2",
      "email":"company@example.com",
      "name":"Example company",
      "enabled":true,
      "companyDefault":true,
      "createdAtUtc":"2026-10-27T07:00:00Z"
    }
  ]
}
```

The automatic recipient appears after a ZIP registration establishes it. An empty list before the first registration does not by itself mean the company profile has no email.

| Field | UI behavior |
| --- | --- |
| `companyDefault=true` | Label Company email; address is managed by the company profile; hide Disable while enabled |
| `companyDefault=false` | Additional recipient; support Disable/Reactivate |
| `enabled=false` | Show disabled state; cancelled mail is not automatically resent |

POST optional additional recipient:

```json
{"email":"rf-team@example.com","name":"RF reporting team"}
```

Email is required; name is optional, maximum 100 characters. POST returns the same object under `recipient` rather than `recipients`. Registering the same email case-insensitively updates/reactivates its existing subscription. If that email is the company default, it remains company-managed and receives one delivery per upload.

Additional recipients must be registered before the upload to receive that report. Later registrations do not backfill historical reports. Changing/deleting the company profile prevents pending company-managed reports from going to its former address; the next new upload subscribes the replacement. Missing/invalid company email permits ZIP/PDF processing but creates no default delivery. Correct the company profile before the next new upload. DELETE cannot recall an email already accepted by SMTP.

## Email delivery monitoring and retry

| Method | Endpoint | Response |
| --- | --- | --- |
| GET | `/api/rf-logs/email-deliveries?uploadId={uploadId}` | 200: region/companyId/deliveries |
| GET | `/api/rf-logs/email-deliveries` | Latest 100 company/region deliveries, newest first |
| POST | `/api/rf-logs/email-deliveries/{deliveryId}/retry` | Empty body; 200 message if eligible, otherwise 409 |

There is no pagination parameter on this delivery endpoint. Do not use uploadId in place of deliveryId for retry.

```json
{
  "region":"IN",
  "companyId":7,
  "deliveries":[
    {
      "id":"b19f2444-8f4e-4d7d-bd0a-e1560583aa07",
      "uploadId":"7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
      "recipientId":"58e6bdee-b843-4a1f-b031-ef2f8fe34cb2",
      "region":"IN",
      "companyId":7,
      "email":"company@example.com",
      "name":"Example company",
      "reportReference":"rf-logs/reports/IN/7/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/<lease-token>/report.pdf",
      "status":"sent",
      "attempts":1,
      "sentAtUtc":"2026-10-27T07:05:00Z",
      "error":null
    }
  ]
}
```

| Delivery status | Display/action |
| --- | --- |
| `pending` | Queued/sending or awaiting automatic retry |
| `sent` | Accepted by SMTP; do not label inbox receipt confirmed |
| `dead_letter` | Automatic attempts exhausted; show error/attempts and Retry if recipient is still eligible |
| `cancelled` | Recipient disabled or former company mailbox; no normal retry action |

Retry success: `{"message":"Report email queued for retry."}`. Default automatic retries: up to 12 attempts, from 30 seconds up to one hour backoff. Manual retry is only for `dead_letter` and an active/current recipient. Email retry does not regenerate the PDF. Default PDF attachment limit is 20 MiB. SMTP acceptance/retry is at least once; a lost acknowledgement can produce a duplicate message.

An empty deliveries list can mean PDF generation is still pending or no eligible recipient existed; it is not proof the email was sent. Email status is independent of `reportStatus` and the optional webhook's `notificationStatus`.

## PDF status/download if the logged-in frontend user is the uploader

| Method | Endpoint | Behavior |
| --- | --- | --- |
| GET | `/api/rf-logs/uploads/{uploadId}` | Event including uploadStatus/reportStatus/error |
| GET | `/api/rf-logs/uploads/{uploadId}/report` | JSON containing signed downloadUrl; 409 before PDF completion |
| POST | `/api/rf-logs/uploads/{uploadId}/report-retry` | Empty body; only failed PDF jobs |

Use the same uploader identity/company/region that registered the upload. A company-admin role does not override this ownership check. There is currently no RF API for listing every company upload or downloading another uploader's PDF as admin. Retain upload IDs from the uploading workflow; the latest delivery list is not a complete upload list.

`reportStatus` values: awaiting_upload, ready, processing, completed, failed. Poll reasonably (e.g. 5-10 seconds while active, with backoff); enable Download only when completed. The report endpoint returns `eventId`, `region`, `contentType`, `downloadUrl`, `expiresAtUtc`. Open/download the signed URL with no backend cookies or auth headers. It expires after 10 minutes by default; fetch a fresh URL when needed. `reportReference` is a private object key, not a public download URL.

If the browser itself uploads a ZIP, it may use POST `/api/rf-logs/upload` with multipart file/uploadId/sessionId and optional subSessionId/projectId. Backend handles storage/completion. Existing request size limits and login/CSRF apply. The app handoff also documents the optional register -> S3 raw PUT -> complete alternative, which uses an unauthenticated S3 HTTP client and configured bucket CORS.

## Wi-Fi details in existing GetNetworkLog

Use the existing GET/POST `/api/MapView/GetNetworkLog` contract. No new Wi-Fi endpoint is required. Captured details are flat optional `wifi_*` properties on their own rows; they are not a new nested object in extra_json. Missing fields remain absent; preserve valid false/zero values in the UI. Do not show two labels for aliases of the same measurement.

Captured scan fields:

```text
wifi_ssid, wifi_bssid, wifi_capabilities, wifi_rssi_dbm, wifi_frequency_mhz,
wifi_timestamp, wifi_distance, wifi_distance_sd, wifi_passpoint,
wifi_channel_bandwidth, wifi_center_freq0_mhz, wifi_center_freq1_mhz,
wifi_standard, wifi_80211mc_responder, wifi_80211az_ntb_responder,
wifi_twt_responder, wifi_radio_chain_infos, wifi_interface_name
```

WiFi_plot companion fields:

```text
wifi_link_speed_mbps, wifi_rx_link_speed_mbps, wifi_channel, wifi_band,
wifi_signal_level_0_4, wifi_matches_lock, wifi_lock, wifi_plot_x, wifi_plot_y,
wifi_plot_timestamp, wifi_tap_timestamp, wifi_drip_timestamp, wifi_idle_before_s,
wifi_interval_s, wifi_interpolation_on, wifi_zoom, wifi_source_session,
wifi_pin_size, wifi_nodata_mode
```

Use existing root dl_tpt/ul_tpt for cleaned plot throughput. wifi_channel_bandwidth is the raw captured enum, not MHz. wifi_timestamp is the raw captured scan timestamp, not Unix time; wifi_plot_timestamp carries the CSV timestamp string. Existing session classification may label a cellular row is_wifi=true; only that row's actual scan/plot source generates wifi_* details. Do not copy another row's scan onto it. Null GPS remains null; plot x/y are not GPS.

Full examples and filter behavior: `get-network-log-wifi.md`. Import paths and mapping: `wifi-plot-import.md`.

## Errors and acceptance

Read HTTP status plus the available message. Most RF errors use `message`; CSRF middleware uses `Status` / `Message`, and validation can return ProblemDetails. 401 requires login; 403/404 means role/ownership/scope; 409 means inspect message; 429/5xx use backoff. Keep IN and TW data separate and clear cached client data after login/region changes. Preserve `(region, uploadId)` for upload references.

Acceptance: admin sees automatic company mailbox after upload registration; optional Add/Disable/Reactivate works; company-managed Disable is unavailable; delivery statuses and eligible retry work; original uploader can open the PDF; ordinary users cannot manage recipients; IN/TW accounts see only their regional/company records; Wi-Fi false/zero/missing fields render correctly.

Backend deployment, server-side configuration and actual phone drive-end calls remain prerequisites. The client receives temporary signed URLs; S3 keys, SMTP passwords and reporting-service keys stay on the backend.
