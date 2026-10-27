# RF automatic reporting: app and frontend developer handoff

Updated against the current backend source on 27 October 2026, including automatic company email. All paths below are relative to `BASE_URL`, the HTTPS origin of the backend that handles the existing login. The deployed origin and release availability must be confirmed before treating these as live endpoints. The Ace Cloud S3 endpoint is not `BASE_URL`.

Role-specific handoffs: [app developer](rf-app-developer-handoff.md) and [frontend developer](rf-frontend-developer-handoff.md). The checked frontend `.env.production` configures `https://s-traccceer.vinfocom.co.in` as the backend origin; this is configuration evidence, not confirmation that the new release is deployed.

## 1. Implemented behavior and verification

The app registers the final drive ZIP, uploads its raw bytes directly to a private Ace Cloud S3 URL, then confirms completion with the backend. The backend verifies the uploaded object, creates a private snapshot, queues report generation in the correct regional database, uses the existing ZIP-to-PDF engine, saves the PDF privately and queues email delivery to the registered company profile email (`tbl_company.email`) plus optional additional report recipients. The app must automate this upload sequence after drive completion. See [automatic drive report workflow](rf-drive-auto-report.md).

| Item | Current state |
| --- | --- |
| Upload, completion, status and PDF APIs | Implemented |
| Recipient registration, unsubscribe and delivery APIs | Implemented; company-admin login required |
| Automatic PDF worker and per-recipient email worker | Implemented; run while the backend is running |
| India/Taiwan storage in separate regional DBs | Implemented; migrations 003/004/005 applied and verified in both locally configured DBs |
| Company profile email as automatic destination | Implemented; new uploads subscribe their company's valid mailbox; IN/TW isolated MySQL checks passed |
| Real Ace Cloud S3 test | Passed ZIP PUT/HEAD/range GET/conditional copy/GET, PDF PUT/GET and cleanup |
| Gmail configuration | Saved privately for Development, explicitly for IN and TW; `smtp.gmail.com:587`, required STARTTLS |
| Real outbound test email | Gmail accepted one email with a real PDF generated from synthetic RF CSV data; inbox/attachment receipt still needs recipient confirmation |
| Frontend/mobile integration | Endpoint handoff provided here; client UI and upload calls still need to be wired |
| Production acceptance | Deployment, server secrets, frontend origins/S3 browser CORS and the full real-login upload-to-email flow still need verification on the deployed server |

The isolated email test did not permanently register its recipient or create a regional upload/job. It does not prove that the production worker pipeline is deployed. Credentials are not included in this handoff.

## 2. Common authentication and regional rules

Use the existing `POST /api/auth/login`, with the application's normal user credentials. These are independent of the backend's Gmail sending account.

```json
{
  "email": "<registered-app-user-email>",
  "password": "<app-user-password>",
  "country_code": "IN"
}
```

Use `TW` for a Taiwan login. Preserve the login cookies, especially `st.auth`; use the region resolved by the successful login (`country` / `source_db`). The RF APIs derive user, company and region from that authenticated identity. Do not choose a database by adding an arbitrary region/company to the upload JSON.

For browser requests to the backend, use the existing authenticated API client (`credentials: "include"` / Axios `withCredentials: true`). For native apps, preserve and resend cookies with the same cookie jar. Cookie-authenticated POST/DELETE requests must send `X-CSRF-TOKEN` matching `st.csrf` when CSRF enforcement is enabled. Reuse the existing application's CSRF handling. GET requests do not require the CSRF header.

| Login region | Connection | Regional records |
| --- | --- | --- |
| IN | `ConnectionStrings:MySqlConnection` | India uploads, jobs, recipients and deliveries |
| TW | `ConnectionStrings:MySqlConnection2` | Taiwan uploads, jobs, recipients and deliveries |

There is no RF fallback between those databases. S3 object paths also contain the region and company. The S3 signing region `ap-south-noi-1` is a storage setting, independent of IN/TW.

Mobile/browser clients receive temporary signed URLs. Keep S3 access keys, SMTP credentials, reporting-service keys and machine-uploader keys on trusted servers.

## 3. App developer: upload and report endpoints

All five endpoints below require the existing logged-in user. Status, completion, report download and report retry are restricted to the same uploader identity that registered the upload, in the same company and region. A company-admin role alone does not override this uploader restriction.

| Method | Endpoint | Request | Success |
| --- | --- | --- | --- |
| POST | `/api/rf-logs/uploads` | Registration JSON below | 200: upload registration and signed PUT URL |
| POST | `/api/rf-logs/uploads/{uploadId}/complete` | Empty body, after successful S3 PUT | 200: event/status JSON |
| GET | `/api/rf-logs/uploads/{uploadId}` | No body | 200: event/status JSON |
| GET | `/api/rf-logs/uploads/{uploadId}/report` | No body | 200: signed PDF download information; 409 if unavailable |
| POST | `/api/rf-logs/uploads/{uploadId}/report-retry` | Empty body | 200: updated event; only a failed report can be requeued |

### A. Register the ZIP

Generate one UUID per ZIP and persist it across retries, app restarts and offline recovery. Replace all sample IDs and the byte length below with actual values.

```http
POST /api/rf-logs/uploads
Content-Type: application/json
X-CSRF-TOKEN: <matching-cookie-token>
```

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

| Field | Requirement |
| --- | --- |
| `uploadId` | Required, non-empty UUID; same ZIP and metadata use the same UUID on retry |
| `fileName` | Required `.zip` filename, at most 180 characters, no directory separators |
| `fileSizeBytes` | Required exact ZIP size in bytes; at least 4 bytes, default maximum 1 GiB (1,073,741,824 bytes) |
| `sessionId` | Required existing session owned by the authenticated user in their regional DB |
| `subSessionId` | Optional string, at most 100 characters |
| `projectId` | Optional positive ID; if supplied, the project must exist in the user's company |
| `userId` | Omit for normal login; the backend uses the logged-in user's ID. If supplied, it must match that login |

`companyId` is not a request field. Example registration response:

```json
{
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventKey": "IN:7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "region": "IN",
  "status": "awaiting_upload",
  "method": "PUT",
  "uploadUrl": "https://<storage-host>/<signed-upload-path-and-query>",
  "requiredHeaders": { "Content-Type": "application/zip" },
  "expiresAtUtc": "2026-10-26T15:30:00Z",
  "completeEndpoint": "/api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/complete"
}
```

Upload URLs last 30 minutes by default. Repeat registration with identical metadata to refresh an expired URL while still awaiting upload. Changed metadata with an existing UUID returns 409. If the returned `status` is already beyond `awaiting_upload`, `uploadUrl` is null: query status rather than uploading again.

### B. Upload the actual ZIP to the returned URL

```http
PUT <exact uploadUrl returned by registration>
Content-Type: application/zip

<raw ZIP binary bytes>
```

This is a request to S3, not to the backend. Use the URL exactly as returned. Send raw bytes, not base64/JSON or multipart form data. Do not attach backend cookies, CSRF headers or backend API keys to this request. Use a separate HTTP client if the normal client injects authentication headers automatically. Browser uploads use `credentials: "omit"` and require the deployed browser origin to be allowed by the bucket's CORS policy.

Wait for a successful S3 HTTP response before calling completion. The actual file bypasses IIS; only the small registration JSON goes through the backend.

### C. Confirm upload completion

```http
POST /api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/complete
X-CSRF-TOKEN: <matching-cookie-token>
```

Send an empty body and the uploader's login cookies. The backend verifies size and ZIP signature, snapshots the accepted input and queues processing. Completion can return `ready`, `processing` or an already terminal report status if a worker/retry ran quickly; always read the response. Repeating completion returns the existing event rather than creating another job.

Uploading a ZIP manually in the storage console, or putting it at an arbitrary bucket path, does not enqueue a report. The app must register, upload to the returned URL and call `/complete`.

### D. Track status and open the PDF

Poll `GET /api/rf-logs/uploads/{uploadId}` at a reasonable interval, for example every 5-10 seconds while the screen is active, with backoff after failures. The backend workers normally check idle queues every 15 seconds; response times also depend on report size and server load.

The exact event response shape is:

```json
{
  "schemaVersion": 1,
  "eventId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventKey": "IN:7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventType": "rf.zip.uploaded",
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "uploadStatus": "completed",
  "reportStatus": "ready",
  "fileName": "drive-test.zip",
  "fileSizeBytes": 524293392,
  "bucket": "dcra-bucket",
  "blobPath": "rf-logs/ready/IN/7/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/<snapshot-id>.zip",
  "etag": "<storage-etag>",
  "uploadedAtUtc": "2026-10-26T15:00:00Z",
  "sessionId": 4266,
  "subSessionId": "20",
  "projectId": 123,
  "userId": 456,
  "companyId": 7,
  "region": "IN",
  "downloadEndpoint": "/api/rf-logs/events/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/download",
  "reportDownloadEndpoint": "/api/rf-logs/uploads/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/report",
  "reportReference": null,
  "error": null,
  "notificationStatus": "pending",
  "notificationAttempts": 0
}
```

All IDs, paths and times above are illustrative. Before completion, `blobPath`, `etag` and `uploadedAtUtc` can be null. `downloadEndpoint` is for a separately authenticated reporting service; it is not an app ZIP download endpoint. Use `reportDownloadEndpoint` for the uploader's PDF.

| `reportStatus` | UI meaning |
| --- | --- |
| `awaiting_upload` | Upload/completion not yet accepted |
| `ready` | ZIP accepted; report waiting in the queue |
| `processing` | PDF generation in progress |
| `completed` | PDF generation finished; enable Download PDF |
| `failed` | Show `error` and offer report retry |

`uploadStatus: "completed"` means ZIP acceptance, not that the PDF or email is finished. `notificationStatus` tracks the optional outbound webhook, not report email. It may stay pending if no webhook is configured.

When `reportStatus` is `completed`, call `GET /api/rf-logs/uploads/{uploadId}/report`. It returns JSON, not the PDF bytes:

```json
{
  "eventId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "region": "IN",
  "contentType": "application/pdf",
  "downloadUrl": "https://<storage-host>/<signed-pdf-path-and-query>",
  "expiresAtUtc": "2026-10-26T15:10:00Z"
}
```

Open/download `downloadUrl` to obtain the PDF. It lasts 10 minutes by default; request a fresh one when expired. Do not persist/log signed URLs as permanent file URLs. No backend credentials go to that S3 URL.

### E. Retry rules

Retry transient network failures, 429 and 5xx with exponential backoff and jitter. Preserve `(region, uploadId)` and the original metadata. For completion 409, inspect `message`: an incomplete upload or wrong size must be corrected before retry. An already registered UUID with different metadata is a conflict, not a transient failure.

`POST /uploads/{uploadId}/report-retry` requeues only `failed` PDF jobs. It does not upload the ZIP again, regenerate an already completed report or resend a sent email.

## 4. Frontend developer: report recipients and email monitoring

These routes require an existing logged-in company admin or super admin (role 2 or 3), with a valid company and region. Ordinary users and server API keys cannot manage recipients. The UI should use the existing admin login and CSRF conventions.

| Method | Endpoint | Purpose | Success |
| --- | --- | --- | --- |
| GET | `/api/rf-logs/recipients` | List recipients, including disabled ones | 200 with `region`, `companyId`, `recipients` |
| POST | `/api/rf-logs/recipients` | Add/update/reactivate one recipient | 200 with `region`, `companyId`, `recipient` |
| DELETE | `/api/rf-logs/recipients/{recipientId}` | Disable subscription and cancel pending emails | 204, no response body |
| GET | `/api/rf-logs/email-deliveries?uploadId={uploadId}` | Inspect a report's email deliveries | 200 with `region`, `companyId`, `deliveries` |
| POST | `/api/rf-logs/email-deliveries/{deliveryId}/retry` | Retry an exhausted failed delivery | 200 with `message`; 409 unless eligible |

POST one recipient at a time:

```json
{
  "email": "rf-team@example.com",
  "name": "RF reporting team"
}
```

`email` is required; `name` is optional (default empty, maximum 100 characters). Re-registering the same email case-insensitively updates/reactivates the existing subscription rather than creating duplicates. Example POST response:

```json
{
  "region": "IN",
  "companyId": 7,
  "recipient": {
    "id": "58e6bdee-b843-4a1f-b031-ef2f8fe34cb2",
    "email": "rf-team@example.com",
    "name": "RF reporting team",
    "enabled": true,
    "companyDefault": false,
    "createdAtUtc": "2026-10-26T14:00:00Z"
  }
}
```

GET recipients wraps the same objects in `recipients: []` instead of `recipient`. Render the `enabled` state. Each object also includes `companyDefault` (boolean). Upload registration automatically adds/reuses the valid company profile mailbox with `companyDefault: true`; no manual recipient POST is required for it. DELETE of an enabled company-managed mailbox returns 409; its destination is managed through the company profile. DELETE of an additional subscription takes the recipient's `id`, not the upload ID.

Optional additional recipients must be registered before the upload registration. The company mailbox is automatically subscribed transactionally during ZIP registration. They receive new reports for that company and region; late registration or a replacement company email does not send historical reports. A changed/deleted company profile fences pending company deliveries to its former address. Missing/invalid company mailboxes allow PDF generation but create no automatic company delivery; correct the profile before the next new upload. Recipients do not need an application login. The uploader is not automatically an email recipient. Taiwan subscriptions and company email come from Taiwan DB, independently of India.

GET email deliveries returns the latest 100 records, newest first. Omit `uploadId` to see the latest company/region deliveries. There is no pagination parameter currently. Example:

```json
{
  "region": "IN",
  "companyId": 7,
  "deliveries": [
    {
      "id": "b19f2444-8f4e-4d7d-bd0a-e1560583aa07",
      "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
      "recipientId": "58e6bdee-b843-4a1f-b031-ef2f8fe34cb2",
      "region": "IN",
      "companyId": 7,
      "email": "rf-team@example.com",
      "name": "RF reporting team",
      "reportReference": "rf-logs/reports/IN/7/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/<lease-token>/report.pdf",
      "status": "sent",
      "attempts": 1,
      "sentAtUtc": "2026-10-26T15:05:00Z",
      "error": null
    }
  ]
}
```

| Delivery `status` | Meaning / frontend action |
| --- | --- |
| `pending` | Waiting, currently leased/sending, or scheduled for automatic retry |
| `sent` | SMTP server accepted the message; inbox receipt is not guaranteed |
| `dead_letter` | Automatic attempts exhausted; allow Retry if the recipient is still enabled |
| `cancelled` | Subscription disabled; queued delivery cancelled |

No delivery row is expected before a completed PDF is reconciled for an eligible recipient. The worker retries failures up to 12 attempts by default, backing off from 30 seconds to at most one hour. A successful PDF remains available even if email fails. Default PDF attachment limit is 20 MiB. SMTP delivery is at least once; a lost acknowledgement can still result in a duplicate message.

Retry response: `{"message":"Report email queued for retry."}`. Email retry does not regenerate the PDF. DELETE cannot recall a message already accepted by SMTP.

Suggested frontend work: an admin recipient form/list with Add, Disable and Reactivate; an email-delivery list with status/attempts/error and eligible Retry; uploader report status, Download PDF and failed-report Retry where the uploader's ID is available. No frontend screens were added by this backend work. There is currently no RF endpoint for listing every upload in a company or letting another admin download an uploader's PDF; retain upload IDs from the upload flow rather than assuming such a route exists.

## 5. Trusted reporting service / operator endpoints

The default workflow generates and emails PDFs inside this backend. App and browser clients do not need the following seven routes. They require a distinct server-side reporting key and a region on every request:

```http
X-RF-Api-Key: <reporting-service-key>
X-RF-Region: IN
```

Use `TW` to access Taiwan. The reporting key is a privileged cross-company service credential; do not distribute it to frontend/mobile code.

| Method | Endpoint | Request / response |
| --- | --- | --- |
| GET | `/api/rf-logs/events?limit=25` | `{"region":"IN","events":[...]}`; ready jobs and processing jobs with expired leases; limit clamped to 1-100 |
| GET | `/api/rf-logs/events/{eventId}` | One event/status object as shown above |
| GET | `/api/rf-logs/events/{eventId}/download` | JSON with `eventId`, `region`, `downloadUrl`, `expiresAtUtc`, `etag`, `fileSizeBytes`; accepted ZIP only |
| GET | `/api/rf-logs/events/{eventId}/report` | JSON with a private PDF `downloadUrl` after completion |
| POST | `/api/rf-logs/events/{eventId}/claim` | Empty body; returns `eventId`, `region`, `leaseToken`, `leaseSeconds: 900`; 409 if claimed/terminal |
| POST | `/api/rf-logs/events/{eventId}/status` | Processing/result JSON below, with `X-RF-Lease-Token`; returns updated event |
| POST | `/api/rf-logs/events/{eventId}/notification-retry` | Empty body; requeue the optional outbound upload notification; returns updated event |

For an external report engine, deploy with `AutomaticReportsEnabled=false` to avoid competing with the built-in PDF worker. Claim first, then use the returned 32-character `leaseToken` in `X-RF-Lease-Token`. Renew before the 15-minute lease expires using `{"status":"processing"}`. Finish with one of:

```json
{"status":"completed","reportReference":"<report-identifier-or-path>"}
```

```json
{"status":"failed","error":"<sanitized-report-error>"}
```

Expired/replaced/terminal leases return 409. A custom external report reference is not automatically a downloadable/email-eligible built-in PDF: built-in download/email paths require the private `rf-logs/reports/{region}/{companyId}/{uploadId}/.../report.pdf` namespace. The existing integrated generator is the intended mode for this project.

An optional trusted server uploader can use the app upload routes with `X-RF-Client-Id` plus its distinct scoped `X-RF-Api-Key`, and an authorized `userId` in the upload body. This is a configured server integration, not a credential to embed in the app. Continue using the same uploader identity for completion/status.

### Optional webhook

If configured, the backend POSTs upload event JSON to `ReportingWebhookUrl` over HTTPS. This is an outbound notification, not an additional inbound app completion route. Headers: `X-RF-Event-Id`, `X-RF-Region`, `X-RF-Timestamp`, `X-RF-Signature: sha256=<hex>`. Verify HMAC-SHA256 over UTF-8 `timestamp + "." + exact_raw_body`, enforce a replay window and deduplicate the signed `eventKey`. Durable retries default to 12 attempts. No external webhook URL is required for the built-in PDF/email workflow.

## 6. Error and acceptance rules

| HTTP status | Expected action |
| --- | --- |
| 400 | Check JSON, UUID/size/IDs, reporting region or CSRF token; validation errors may use standard ASP.NET ProblemDetails |
| 401 | Login/service credential missing or invalid; reauthenticate |
| 403 | Company/user/session/project/admin-role/region authorization failure |
| 404 | Upload/recipient missing or outside this caller's scope |
| 409 | Inspect `message`: metadata conflict, incomplete/changed ZIP, unavailable PDF, invalid retry or lease conflict |
| 429 | Back off; honor `Retry-After` when provided |
| 500/503 | Retry transient failures with backoff; operator checks configuration, S3 and regional schema/DB |

Most RF errors use `{"message":"..."}`; existing middleware/validation responses can differ. Read the status and available error fields. Store `eventKey` or `(region, uploadId)` rather than assuming UUID alone distinguishes both regional DBs.

Before production acceptance, deploy the current backend, configure S3 and SMTP secrets on that server for both regions, verify SQL migrations 003/004/005, allow the frontend origin and any browser S3 CORS needs, and keep IIS workers running. Local user secrets are not copied into production by publishing. Then test independently with real IN and TW accounts: confirm the company profile email, optionally subscribe additional recipients, have the session owner register/upload the final drive ZIP and call `/complete` automatically, wait for completed, download the PDF, inspect delivery status and confirm company inbox/PDF receipt. Repeat completion to verify the same event is returned.

Existing `RemoteLogZipUrlTemplate` / `TaiwanRemoteLogZipUrlTemplate` settings serve the existing remote-log flow. They are not replacements for the new registration/PUT/completion sequence and do not subscribe old storage uploads automatically.

Runnable request examples: [rf-log-integration.http](rf-log-integration.http). Detailed backend/operations reference: [rf-log-integration.md](rf-log-integration.md). Server email configuration: [rf-report-mailbox-setup.md](rf-report-mailbox-setup.md).
