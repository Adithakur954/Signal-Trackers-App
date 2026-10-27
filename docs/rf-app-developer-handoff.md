# App developer handoff: automatic drive PDF and company email

Updated: 27 October 2026. API contracts verified against this backend source. The production deployment and phone-side implementation still need acceptance testing.

## Connection and authentication

The checked frontend `.env.production` currently configures this backend origin:

```text
BASE_URL=https://s-traccceer.vinfocom.co.in
```

Use the backend HTTPS origin that handles the existing app login. Confirm deployment of these routes before testing; this document does not confirm that the current production server has this release. The Ace Cloud storage endpoint is a different origin; use only the signed URL returned by the backend for ZIP upload.

For this workspace's local testing, `rf-client-integration.http` defaults to `http://localhost:5224`. Start the backend with its Development launch profile (`dotnet run --launch-profile http`) so local user-secrets load. To test production, explicitly change the request file's `baseUrl` to the verified deployed origin. A local frontend page can still call production if its API base URL points there.

Existing login:

```http
POST {{BASE_URL}}/api/auth/login
Content-Type: application/json
```

```json
{"email":"<app-user-email>","password":"<app-user-password>","country_code":"IN"}
```

Use `TW` for Taiwan. Use the successful login's resolved `country` / `source_db`. Preserve all login cookies in the app cookie jar, including `st.auth` and session cookies. Send `X-CSRF-TOKEN` matching `st.csrf` for authenticated backend POST requests when CSRF is enabled. Existing login determines user, company and region; omit `companyId`, destination email and credentials from RF upload requests. No separate RF API key is needed with a valid login.

## Required drive-end sequence

When the drive ends, close/finish writing the final ZIP, persist a stable UUID and its metadata, then run these steps automatically. A user should not have to press Generate PDF or Send Email.

| Step | Request | Body |
| --- | --- | --- |
| 1 | `POST {{BASE_URL}}/api/rf-logs/uploads` | Registration JSON |
| 2 | `PUT <uploadUrl returned by step 1>` | Raw ZIP bytes |
| 3 | `POST {{BASE_URL}}/api/rf-logs/uploads/{uploadId}/complete` | Empty |

### 1. Register the final ZIP

```json
{
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "fileName": "drive-test.zip",
  "fileSizeBytes": 524293392,
  "sessionId": 4266,
  "subSessionId": "20",
  "projectId": 123
}
```

All values are examples. Replace the IDs and size with actual values.

| Field | Rule |
| --- | --- |
| `uploadId` | Required non-empty UUID; generate once per final ZIP and retain across retries/restarts |
| `fileName` | Required `.zip` filename; maximum 180 characters; no path separators |
| `fileSizeBytes` | Required exact final ZIP size; default accepted range 4 bytes to 1 GiB |
| `sessionId` | Required existing session owned by this logged-in user in the resolved region |
| `subSessionId` | Optional string, maximum 100 characters; omit if unavailable |
| `projectId` | Optional positive ID belonging to the authenticated company; omit if unavailable |
| `userId` | Omit for normal login; backend uses the logged-in user |

Response (200):

```json
{
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventKey": "IN:7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "region": "IN",
  "status": "awaiting_upload",
  "method": "PUT",
  "uploadUrl": "https://<storage-host>/<signed-path-and-query>",
  "requiredHeaders": {"Content-Type":"application/zip"},
  "expiresAtUtc": "2026-10-27T08:30:00Z",
  "completeEndpoint": "/api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/complete"
}
```

Dates and URLs are illustrative. The upload URL lasts 30 minutes by default. While status is `awaiting_upload`, repeat registration with identical metadata to get a fresh URL. If status has advanced, `uploadUrl` is null; check status rather than uploading again. Reusing a UUID with different metadata returns 409.

### 2. Upload the bytes directly to S3

```http
PUT <exact uploadUrl>
Content-Type: application/zip

<raw binary bytes of the final ZIP>
```

Use the URL unchanged, including its signed query. Send raw bytes, not multipart/form-data, base64 or JSON. Use a separate storage HTTP client that sends no backend cookies, CSRF token or backend authentication headers. For browser clients use `credentials: "omit"`; bucket CORS must allow the browser origin and PUT/Content-Type. Native apps do not use browser CORS.

Wait for a successful S3 2xx response. Keep the local ZIP until backend completion is confirmed so network failures can be recovered.

### 3. Notify backend after successful PUT

```http
POST {{BASE_URL}}/api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/complete
X-CSRF-TOKEN: <matching st.csrf cookie>
```

Send an empty body and the original uploader's login cookies. Backend verifies the ZIP size/signature, makes a private snapshot and queues the report. Repeated completion returns the same event. It does not create another report job. If acceptance is already recorded, resume status checks instead of making another upload UUID.

Manual console uploads to arbitrary bucket paths do not trigger this workflow. The backend receives no phone drive-end signal until the app performs the sequence above.

## Optional app status, PDF and report retry

| Method | Endpoint | Use |
| --- | --- | --- |
| GET | `/api/rf-logs/uploads/{uploadId}` | Read upload/report status |
| GET | `/api/rf-logs/uploads/{uploadId}/report` | Get a temporary PDF download URL after completion |
| POST | `/api/rf-logs/uploads/{uploadId}/report-retry` | Requeue a failed PDF job; empty body |

These routes require the same uploader identity/company/region as registration. A company admin does not automatically have access to another user's upload. Suggested polling: every 5-10 seconds while the status screen is active, with backoff. Idle report/email workers normally poll every 15 seconds; report generation is asynchronous.

Status response excerpt (the full event contract is in `rf-log-developer-handoff.md`):

```json
{
  "eventKey":"IN:7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "uploadStatus":"completed",
  "reportStatus":"ready",
  "region":"IN",
  "sessionId":4266,
  "companyId":7,
  "reportDownloadEndpoint":"/api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/report",
  "error":null
}
```

| `reportStatus` | Meaning |
| --- | --- |
| `awaiting_upload` | ZIP/completion not accepted |
| `ready` | ZIP accepted; PDF queued |
| `processing` | PDF generating |
| `completed` | PDF ready; email has a separate delivery status |
| `failed` | Show error; allow report retry |

`uploadStatus=completed` confirms ZIP acceptance only. `notificationStatus` tracks an optional server webhook, not email delivery. Ordinary app users cannot call the company-admin email delivery routes.

PDF response (200, JSON rather than PDF bytes):

```json
{
  "eventId":"7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "region":"IN",
  "contentType":"application/pdf",
  "downloadUrl":"https://<storage-host>/<signed-pdf-path-and-query>",
  "expiresAtUtc":"2026-10-27T08:10:00Z"
}
```

Download the PDF from `downloadUrl` without backend credentials. Default validity is 10 minutes; request another URL after expiry. A report retry is allowed only for `failed` reports and does not resend a successful email.

## ZIP contents and Wi-Fi companion

Keep the existing supported network-log CSV format and timestamps. The report engine accepts timestamped network filenames such as `NetworkLog_20261027_143801.csv` / `NetworkLogUnsent_4266.csv`; a ZIP must contain usable network-log data for the PDF engine. Preserve L3/Event and other supported companion files when available.

Include `WiFi_plot.csv` unchanged in the same ZIP when captured. The backend imports it into the existing authorized regional session's network-log table; no new app endpoint is required for Wi-Fi plots. A Wi-Fi-only ZIP without usable network-log data is not a newly supported PDF format. Do not substitute CSV plot x/y for missing GPS. See `wifi-plot-import.md` and `get-network-log-wifi.md`.

## Company email and recovery

The backend automatically uses the valid `tbl_company.email` for the authenticated company in its IN/TW DB. No destination email is submitted by the app. Optional company-admin recipients can receive new reports too. The sending SMTP account is separate from both the app user's login and company destination.

Persist `(region, uploadId)`, metadata, local ZIP path and workflow stage for offline/restart recovery. Do not change the ZIP contents/size under the same UUID. Use exponential backoff for network/5xx/429 errors and honor Retry-After if provided. For 401, restore login with the same identity/region before resuming. For 409 inspect the message: metadata conflicts and incorrect ZIP sizes need correction, not blind retry.

| HTTP | Action |
| --- | --- |
| 400 | Fix validation/CSRF errors |
| 401 | Authenticate again |
| 403/404 | Check session ownership, original uploader identity and region |
| 409 | Read error message; PDF may not be ready or retry/metadata may be invalid |
| 429 | Back off |
| 500/503 | Retry transient failures; backend operator checks configuration/service health |

## Acceptance checklist

- IN login: finish drive, final ZIP registers/uploads/completes without a manual report action.
- PDF reaches completed and is downloadable by the uploader.
- Company-admin delivery view shows the company's registered mailbox; recipient confirms inbox/PDF receipt.
- Repeat completion/restart recovery with the same UUID; it returns the existing event.
- Repeat independently with a TW login. No regional fallback.
- If present, WiFi_plot data appears on its own Wi-Fi rows through GetNetworkLog.

Do not embed S3 access keys, SMTP passwords or reporting-service API keys in the app. No external webhook receiver is needed for the built-in PDF/email flow.
