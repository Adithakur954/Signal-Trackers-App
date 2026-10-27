# RF ZIP upload and reporting integration (Ace Cloud S3)

This integration accepts private ZIP uploads into Ace Cloud S3, records upload-complete events in the user's regional database, generates PDF reports automatically using the existing ZIP report engine, and emails the PDF to the company's registered profile email (`tbl_company.email`) plus any optional additional report recipients. Short-lived report download links, optional outbound webhooks and an external reporting API are also available. Existing network-log APIs, frontend, and report calculations are unchanged.

For the app/frontend handoff with current readiness, all endpoint contracts, request/response examples and client responsibilities, use [RF developer handoff](rf-log-developer-handoff.md).

## Endpoints to share with the app team

Use the deployed backend's HTTPS origin as `BASE_URL`. These routes are source-code additions; deploy and configure them before sharing them as live URLs.

| Method | Route | Caller | Purpose |
| --- | --- | --- | --- |
| POST | `/api/rf-logs/uploads` | Logged-in app | Register ZIP and obtain presigned PUT URL |
| POST | `/api/rf-logs/uploads/{uploadId}/complete` | Same logged-in user | Verify uploaded ZIP and publish one event |
| GET | `/api/rf-logs/uploads/{uploadId}` | Same logged-in user | Upload/report status |
| GET | `/api/rf-logs/uploads/{uploadId}/report` | Same logged-in user | Short-lived PDF download link after completion |
| POST | `/api/rf-logs/uploads/{uploadId}/report-retry` | Same logged-in user | Requeue a failed report |
| GET | `/api/rf-logs/recipients` | Logged-in company admin | List this company's report subscriptions |
| POST | `/api/rf-logs/recipients` | Logged-in company admin | Register/reactivate a report email recipient |
| DELETE | `/api/rf-logs/recipients/{recipientId}` | Logged-in company admin | Disable recipient and cancel queued emails |
| GET | `/api/rf-logs/email-deliveries?uploadId={uploadId}` | Logged-in company admin | Inspect per-recipient delivery status (latest 100) |
| POST | `/api/rf-logs/email-deliveries/{deliveryId}/retry` | Logged-in company admin | Requeue a dead-letter email for an active recipient |
| GET | `/api/rf-logs/events?limit=25` | Reporting service | Poll available jobs if webhook is not configured |
| GET | `/api/rf-logs/events/{eventId}` | Reporting service | Event/report/delivery status |
| GET | `/api/rf-logs/events/{eventId}/download` | Reporting service | Obtain fresh private GET URL |
| GET | `/api/rf-logs/events/{eventId}/report` | Reporting service | Obtain the automatic PDF download link |
| POST | `/api/rf-logs/events/{eventId}/claim` | Reporting service | Acquire a 15-minute processing lease |
| POST | `/api/rf-logs/events/{eventId}/status` | Reporting service | Renew lease or record completion/failure |
| POST | `/api/rf-logs/events/{eventId}/notification-retry` | Reporting operator | Retry the upload-complete notification |

The upload ID is also the reporting event ID within its region. The globally scoped deduplication key is `eventKey` (`IN:<UUID>` or `TW:<UUID>`). Generate a UUID once per ZIP on the app and save it until completion is acknowledged. Keep the same ID and metadata across retries; new UUIDs intentionally create new jobs.

## Authentication and company ownership

The default app flow uses the existing `POST /api/auth/login` authentication cookie. The user signs in with their email/password as they do today. The backend obtains user ID, company ID, and region from verified login claims; no company ID or email is trusted from the upload payload. Cookie-authenticated POSTs also send the existing `X-CSRF-TOKEN` header matching the `st.csrf` cookie when CSRF is enabled. Browser clients use their normal credentialed API client.

The registered session must belong to the logged-in user. An optional project must exist in that company. All ownership checks, upload registrations, reporting events, leases, report results, and notification retries stay in the user's regional database: `IN` uses `MySqlConnection` (India); `TW` uses `MySqlConnection2` (Taiwan). There is no central event database and no fallback between regions. A missing/unavailable Taiwan DB cannot cause Taiwan records to be written to India. A user can only query/complete uploads they registered in their login region.

For a trusted server-side app integration without a user cookie, an optional company-scoped uploader credential is supported:

```http
X-RF-Client-Id: drive-app
X-RF-Api-Key: <uploader service key>
```

This mode takes `userId` in the body and verifies its company/session ownership. Configure an optional `UserId` to restrict the key to one user. Never embed shared server integration credentials or S3 credentials in a distributed mobile/browser client.

Reporting routes require both `X-RF-Api-Key: <reporting service key>` and `X-RF-Region: IN` or `X-RF-Region: TW`. Every request opens only that region's database; missing/invalid region returns 400. Poll each region separately. This separate trusted backend credential can consume events across companies and must remain on the reporting server. A user's login cookie and an uploader key cannot call reporting endpoints. Upload APIs ignore reporter region headers and derive their region from verified login claims or a configured machine client. Missing storage configuration returns 503, even in Development.

## App upload sequence

1. Existing user logs in. Register the ZIP using the actual ZIP byte length:

```json
{
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "fileName": "RF-drive-20261026.zip",
  "fileSizeBytes": 524293392,
  "sessionId": 4266,
  "subSessionId": "20",
  "projectId": 123
}
```

`subSessionId` and `projectId` are optional. The example IDs must be replaced by real records. `userId` is unnecessary in the logged-in flow. If explicitly supplied, it must match the login.

2. The response supplies `uploadUrl`, `method: PUT`, `requiredHeaders`, `expiresAtUtc`, and `completeEndpoint`. Send the ZIP as **raw binary bytes** to `uploadUrl` with `Content-Type: application/zip`. Do not send multipart form data or app API-key/cookie headers to storage. Default limit is 1 GiB; URL lifetime is 30 minutes. The ZIP bypasses IIS because it goes directly to storage; the metadata request is limited to 16 KiB. If the URL expires, repeat registration with the same UUID/payload to obtain a new one.

3. Wait until the storage PUT returns success. Then POST to `completeEndpoint` with the same app authentication and an empty body. Do not call completion merely because capture ended or an upload started.

4. The server verifies object existence, exact byte length and a ZIP signature; it creates and verifies a separate private snapshot using a conditional copy. The accepted object cannot be overwritten by the original app PUT URL. Full archive integrity and safe extraction remain the report processor's responsibility. Only the successful atomic database transition to `ready` publishes the event.

5. A repeated completion returns the existing event. A changed body with an existing upload ID returns 409. Offline app uploads should persist the UUID and retry completion after reconnecting.

Retry network errors, 429, and 5xx with exponential backoff plus jitter. Completion 409 can mean the upload is not available, its size does not match, or it changed during verification: finish/re-upload the original ZIP before retrying. Do not blindly retry invalid metadata, identity failures, or registration conflicts. Respect `Retry-After` if supplied.

## Reporting event

```json
{
  "schemaVersion": 1,
  "eventId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventKey": "IN:7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "eventType": "rf.zip.uploaded",
  "uploadId": "7d8d4442-21b3-4a60-a3dc-b7b96c9f062c",
  "uploadStatus": "completed",
  "reportStatus": "ready",
  "fileName": "RF-drive-20261026.zip",
  "fileSizeBytes": 524293392,
  "bucket": "dcra-bucket",
  "blobPath": "rf-logs/ready/IN/7/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/<snapshot-id>.zip",
  "etag": "<storage-etag>",
  "uploadedAtUtc": "2026-10-26T08:30:00Z",
  "sessionId": 4266,
  "subSessionId": "20",
  "projectId": 123,
  "userId": 456,
  "companyId": 7,
  "region": "IN",
  "downloadEndpoint": "/api/rf-logs/events/7d8d4442-21b3-4a60-a3dc-b7b96c9f062c/download"
}
```

The event carries the stable object path, not an expiring GET URL. Fetch `downloadEndpoint` with the reporting credential and `X-RF-Region` matching the event when ready to download. That response provides a presigned `downloadUrl`, expiry, expected size, and ETag. Default GET validity is 10 minutes; refresh it if expired. Treat presigned URLs as temporary bearer credentials and avoid logging them. ETag is an object identifier, not necessarily a file MD5/checksum.

## Automatic PDF reports (default)

With `Enabled=true`, valid storage credentials and `AutomaticReportsEnabled=true`, the backend handles the reporting pipeline itself. No external reporting URL is required. Independent India and Taiwan workers claim their own regional jobs, download the accepted ZIP with an ETag condition, call the existing `UnifiedMapZipReportController` PDF engine, save a private PDF under `rf-logs/reports/{region}/{companyId}/{uploadId}/{leaseToken}/report.pdf`, and mark that regional event completed. Input ZIP records are not imported into unrelated session tables.

The worker renews its processing lease every minute. A restarted worker can recover an expired lease after 15 minutes; a stale worker cannot publish over a newer job. Temporary ZIPs are removed after processing. Poll the uploader status endpoint until `reportStatus` becomes `completed`, then call the uploader `/report` endpoint with the same login to obtain a PDF download URL. `reportDownloadEndpoint` is also included in the event. Failed jobs record a sanitized error and can be retried using the owner-only `/report-retry` endpoint.

When the accepted ZIP also contains `WiFi_plot.csv`, the built-in worker imports it into `tbl_network_log` for that upload's existing regional session/company after archive validation/PDF generation and before publishing completion. Source ownership is rechecked, rows use batched inserts and stable-source deduplication, and existing `GetNetworkLog` returns their parsed Wi-Fi fields. This work runs in the background; registration/completion do not download/process the ZIP synchronously. See [Wi-Fi plot import](wifi-plot-import.md).

Automatic reports use the same network-log CSV input and calculations as the existing manual ZIP PDF endpoint. ZIPs without usable network-log data fail rather than produce a partial report. Resource limits default to 5,000 archive entries, 256 MiB per entry, 2 GiB total expanded data (`MaxExpandedZipBytes`), and 250,000 CSV lines (`MaxAutomaticReportRows`). Increase configurable limits only after sizing the reporting server. The backend needs temporary disk space for one compressed ZIP per active region, plus memory for the existing report engine.

Set `AutomaticReportsEnabled=false` when a separate reporting tool should consume the jobs instead. Both reporting modes share the same database leases; run one intended mode to avoid competing workers. Upload-complete webhooks remain available in automatic mode, including when a report finishes before its notification is delivered.

## Automatic email to registered report recipients

The existing ZIP-to-PDF engine is used. Each new upload registration automatically subscribes its company's valid `tbl_company.email` from that same regional DB, in the same transaction as the upload record. No separate recipient registration is needed for this company mailbox. The company is resolved from the authenticated uploader, never a request-supplied destination. The uploader and other application users are not automatically subscribed.

The app must automatically register the final drive ZIP, PUT its bytes to S3 and call `/api/rf-logs/uploads/{uploadId}/complete` after drive completion. The backend then generates the PDF and queues the attachment email without a manual report or send action. This backend does not independently detect the phone's drive-end event. See [automatic drive report workflow](rf-drive-auto-report.md).

A company admin can optionally register additional recipients before the drive upload is registered:

```http
POST /api/rf-logs/recipients
Content-Type: application/json
X-CSRF-TOKEN: <matching st.csrf cookie>

{"email":"rf-team@example.com","name":"RF reporting team"}
```

Existing admin roles 2 and 3 are accepted, scoped strictly to the admin's company and login region. An app uploader key or reporting-service key cannot manage recipients. India and Taiwan each store their own `rf_log_report_recipients` and `rf_log_email_deliveries` tables. The same email can subscribe independently in different companies/regions. Address matching is case-insensitive, and repeated registration updates/reactivates the same recipient rather than creating duplicates. Recipients do not need a separate app login.

With `ReportEmailsEnabled=true` (default), an independent dispatcher reconciles completed reports into a durable per-recipient delivery queue. It sends one email per recipient, with the actual PDF attached. Configure `RfLogIntegration:Smtp:IN` and `RfLogIntegration:Smtp:TW` separately in server configuration. Each entry has `Host`, `Port`, `UserName`, `Password`, `FromAddress` and `EnableSsl` (default true). Port 465 uses SSL/TLS immediately; other TLS-enabled ports use required STARTTLS (typically 587). If an entry is absent, the sender reads active `m_email_setting` SMTP configuration from that same regional DB. An explicitly configured but invalid entry fails rather than silently using another account. It never uses the legacy `received_email_on` default list or falls back to India's SMTP configuration for Taiwan. No real recipients are pre-registered by the migration.

On IIS, the dedicated helper writes only the selected region's SMTP environment settings into the deployed web.config and privately prompts for the password/app password:

```powershell
.\scripts\Configure-RfLogReportEmail.ps1 -BackendPath 'C:\inetpub\wwwroot\Backend' -Region IN -SmtpHost 'smtp.YOUR-MAIL-PROVIDER.com' -Port 587 -FromAddress 'reports@YOUR-DOMAIN.com' -UserName 'reports@YOUR-DOMAIN.com'
```

Run again with `-Region TW` for Taiwan. Supply a real authorized sender/account and the provider's SMTP settings; no SMTP credentials are bundled. The helper does not send a test email or change the legacy database SMTP settings. Optional additional recipients must be registered before registering drive uploads; the company profile email is included automatically. On 26 October 2026, neither locally configured regional database had active legacy SMTP settings, so actual outbound delivery still requires server configuration and verification.

The selected sender is now Gmail, configured privately for Development for both regions. Required STARTTLS and authentication passed, and Gmail SMTP accepted the authorized synthetic-report test email. Inbox receipt and the deployed worker flow still require confirmation. Server configuration remains separate from local user secrets. The earlier company-domain mailbox was only a proposal; see [RF report mailbox setup](rf-report-mailbox-setup.md) for current Gmail settings and server setup.

Subscribers receive reports for their company whose upload registration happened after subscription registration. Newly registered recipients are not sent historical completed reports. A unique `(region,uploadId,recipientId)` constraint prevents reconciliation from duplicating deliveries; restarts recover queued emails even if the report worker stopped immediately after saving its PDF. Email retry never regenerates the PDF. Each recipient's success/failure is recorded independently; a failed address cannot block the other recipients.

Delivery retries start after 30 seconds, back off to at most one hour and stop at `MaxEmailAttempts` (default 12), leaving `dead_letter` for administrator inspection/retry. A five-minute fenced delivery lease prevents concurrent workers from normally sending the same queued message; each attempt has a two-minute total timeout. A stable Message-ID is included. SMTP delivery is at least once: a crash or lost acknowledgement after the mail server accepts a message can still produce a repeated email. `sent` means the SMTP server accepted the email, not proof of inbox delivery; bounce handling is outside this integration.

Removing an additional recipient disables its subscription and cancels queued delivery tokens atomically. The recipient list includes `companyDefault: true` for a company-managed mailbox; DELETE returns 409 for an enabled company-managed recipient. Change that address in the company profile instead. Before sending a company-managed delivery the worker rechecks the current company email. A changed/deleted profile prevents sending to the former mailbox; the next new upload subscribes the replacement and disables the former company mailbox. A replacement email does not receive historical reports. An address already manually subscribed is reused as the company default, producing one delivery rather than two. Missing/invalid company emails do not block upload/PDF processing; fix the company profile before the next new drive upload, or register a valid optional recipient before uploading.

An email already handed to the SMTP server cannot be recalled. Re-enabling an additional recipient starts a new subscription window; cancelled historical deliveries are not automatically resent. Only dead-letter deliveries to an active recipient can be explicitly retried. `email-deliveries` is a separate status from PDF generation: a mail failure leaves the successfully generated PDF intact.

The default PDF attachment cap is 20 MiB (`MaxEmailAttachmentBytes`); allow for base64 expansion and your SMTP provider's message-size limit. An oversized report records a delivery error rather than emailing a partial attachment. Adjust the cap only if the mail provider supports the larger message. Credentials stay in the server's regional mail configuration and are not exposed through these APIs. No frontend registration screen was added; use the documented authenticated API requests.

## Reliable dispatch and processing

Configure `ReportingWebhookUrl` to an HTTPS endpoint on the reporting service and `WebhookSigningSecret` to at least 32 characters. The backend POSTs the event JSON with:

```http
X-RF-Event-Id: <eventId>
X-RF-Region: <IN-or-TW>
X-RF-Timestamp: <UTC Unix seconds>
X-RF-Signature: sha256=<lowercase-hex-HMAC-SHA256>
```

The signature input is `timestamp + "." + exact_raw_request_body`, UTF-8 encoded, signed with the shared webhook secret. Verify it using constant-time comparison and reject timestamps outside a short replay window (for example five minutes). Use the signed body's region and `eventKey` for dispatch/deduplication, and reject mismatches with the region header. The receiver must durably accept/deduplicate `eventKey` before returning 2xx. Redirects are not followed. Requests time out after 30 seconds.

Dispatch is **at least once**, not exactly once. Delivery attempts are durable in each region's MySQL database, start within roughly 15 seconds while the backend is running, and back off from 30 seconds to a maximum of one hour. India and Taiwan dispatch loops run independently; an error or unavailable database in one region does not stop the other region's delivery. The default maximum is 12 attempts, then `dead_letter`; reporting operators can inspect status and retry with the appropriate region header. IIS restarts lose no accepted database events. IIS must be kept running (AlwaysRunning/preload or an external hosted worker) for immediate dispatch; jobs wait safely while the process is stopped.

If no reporting webhook receiver exists, leave its URL blank. The built-in worker still generates PDFs. In external reporting mode, the separate tool can poll `GET /events`; no externally reachable receiver is required for polling.

For an external reporting tool (`AutomaticReportsEnabled=false`), either delivery mode must:

1. Persistently deduplicate on `eventKey` (equivalently the tuple `region,eventId`) and POST `/claim` with the matching `X-RF-Region` before generating a report. Only one worker gets a lease; others receive 409.
2. Save the returned `leaseToken`, get a fresh download URL, download and validate the ZIP, and run its reporting engine.
3. Send `X-RF-Lease-Token` with `/status`. Use `{"status":"processing"}` to renew before 15 minutes expires. An expired job becomes claimable again; stale workers cannot update the newer job's status.
4. Finish with `{"status":"completed","reportReference":"<report-id-or-path>"}` or `{"status":"failed","error":"<sanitized error>"}`. Do not include secrets in errors. Completed/failed records are terminal and not polled again.
5. Store report output under the region and event ID with idempotent writes. Leases and delivery deduplication cannot by themselves guarantee exactly-once external side effects, particularly if a worker dies after saving its report.

## Server deployment

1. Rotate any S3 credentials shared in chat. Keep the bucket private. The backend credential needs GetObject and PutObject (including conditional copy) on the staging, ready, report and check prefixes. The deployment storage check also needs DeleteObject on `rf-logs/checks/*` to remove its temporary objects. ListBucket, if permitted, lets the provider distinguish absent objects from access-denied responses. Bucket creation/credentials must be managed in Ace Cloud by an authorized storage administrator; no public ACL is needed.
2. Confirm `dcra-bucket` exists at `https://s3-noi.aces3.ai`. Its configuration screen supplied on 26 October 2026 confirms signing region `ap-south-noi-1`; the options, example configuration and deployment helpers now use that value. `RfLogIntegration:Region` is the S3 signing region, independent of the login/database regions IN and TW. For another endpoint, use its actual configured signing region. `ForcePathStyle` defaults to true. If the provider lacks conditional copy or byte-range GET support, completion fails closed; verify compatibility in staging.
3. Apply migrations `003_rf_log_upload_events.sql`, `004_rf_log_report_email.sql` and `005_rf_log_company_email.sql` separately to **both** `ConnectionStrings:MySqlConnection` (India) and `ConnectionStrings:MySqlConnection2` (Taiwan), or use the explicit `--rf-log-db-migrate` command below (it applies and verifies these migrations, checking for the 005 column before applying it). Each DB stores only its own regional records. Each runtime DB account needs SELECT/INSERT/UPDATE on its local RF tables and SELECT for its ownership/SMTP tables and `tbl_company`; the migration account needs CREATE TABLE and ALTER TABLE. The integration does not create schema at normal app startup. If an earlier centralized version was already deployed, migrate existing TW event records from the India table to Taiwan before switching; do not delete the source until the regional copy is verified.
4. Publish the backend including the new AWSSDK.S3 dependencies. The integration remains disabled until configured; unrelated app startup/login does not require S3.
5. Configure `RfLogIntegration` using the production example or environment variables. Use `RfLogIntegration__AccessKey` and `RfLogIntegration__SecretKey`; standard `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` are fallback sources. Do not commit credentials. Reporting API keys and webhook signing secrets must be distinct, random values of at least 32 characters.
6. Build a release with `scripts/Build-RfLogRelease.ps1`. Copy `artifacts/rf-log-release.zip` to the IIS server and extract it outside the live backend folder. The package excludes development appsettings and preserves the server's existing settings. In administrator PowerShell in the extracted package, run:

```powershell
.\scripts\Deploy-RfLogIntegration.ps1 -PackagePath .\backend -BackendPath 'C:\inetpub\wwwroot\Backend' -AppPoolName 'YOUR_ACTUAL_POOL_NAME' -ConfigureStorage
```

Use the pool name from IIS Manager > Sites > Backend > Basic Settings. The script backs up live configuration, stops only that pool, copies the release while preserving live settings and data, fixes the single DLL argument, grants that pool write access to logs/App_Data/UploadedExcels, applies the RF schema to both configured production databases, runs a real Ace Cloud storage round trip, and starts the pool only if those checks pass. On failure it leaves the pool stopped and prints the failing check. The configuration backup does not include previous binaries. Open the site once to start the hosted workers. AlwaysRunning and a zero idle timeout are set; install/configure IIS Application Initialization and preload for automatic managed-app startup after server restarts.

The invoked configure helper prompts privately for rotated S3 credentials, updates that site's `web.config` and prints newly generated reporting credentials for secure handoff. Re-running it rotates those application keys. To configure storage separately, run `scripts/Configure-RfLogIntegration.ps1 -BackendPath ...`; add `-ExternalReporting` to disable built-in PDFs. Optional `-CompanyId 7 -ClientId drive-app -ClientRegion IN` enables a server-to-server uploader; use a distinct client ID and `-ClientRegion TW` for a Taiwan machine client. Normal email login requires no company ID setup.

Add `-ReportingWebhookUrl 'https://YOUR-REPORTING-SERVICE/events/rf-zip'` to the configure helper if a receiver exists. Protect the deployed `web.config` with administrator/app-pool file permissions; it contains server secrets.

7. Configure bucket lifecycle/retention: incomplete/staging objects may be removed after a suitable retry window (for example seven days). Do not apply that short TTL to ready snapshots. A rare concurrent completion or failed database write can leave an unreferenced ready snapshot; clean those separately by checking database references. Never delete referenced input while a report/download is in progress.
8. For browser direct upload/download, configure bucket CORS for the real frontend origins and PUT/GET/HEAD with Content-Type. Native/server clients do not need browser CORS. Backend frontend CORS remains governed by the existing allowed-origin settings.

## Verification and handoff

Runnable request examples are in `docs/rf-log-integration.http`. Use actual session/project IDs for the logged-in account. Publish both the API and database migration before testing the live flow.

Operator commands run without starting the website or its background workers:

```powershell
dotnet .\SignalTracker.dll --rf-log-db-check --environment Production
dotnet .\SignalTracker.dll --rf-log-db-migrate --environment Production
dotnet .\SignalTracker.dll --rf-log-storage-check --environment Production
```

The standalone CLI reads appsettings/environment variables, not IIS `web.config` variables automatically. The deployment script temporarily imports the site's variables for these checks. The storage command tests presigned ZIP PUT, HEAD, range GET, conditional snapshot copy, presigned ZIP GET, report PUT/GET and cleanup under a random check prefix. It never queues an event or modifies session data.

Local storage validation on 26 October 2026: the supplied Ace Cloud endpoint/bucket and credentials are configured in the existing Development user-secret store, outside repository appsettings, with signing region `ap-south-noi-1` confirmed by the bucket configuration screen. Initial requests returned HTTP 403 `InvalidAccessKeyId`; the console subsequently showed the configured access key was disabled. After the operator enabled that key, the complete live storage check passed: presigned ZIP PUT, HEAD, ZIP byte-range GET, conditional snapshot copy, presigned snapshot GET, private PDF PUT and presigned PDF GET. All temporary check objects were removed. This verifies storage access from the development environment; the deployed IIS server still requires its own configuration, and this check does not generate reports or send emails.

On 26 October 2026, both RF migrations and schema verification succeeded against the locally configured India database `Stracer` and Taiwan database `TaiwanDB`. Real MySQL recipient/queue/lease/retry/unsubscribe tests also passed, with test rows removed and no SMTP calls made. A deployment using different DB connections must run the same schema checks against those databases.

On 27 October 2026, migration 005 (`company_default`) was also applied and verified in both configured DBs. The new `--rf-log-company-email-db` regression passed in IN and TW using connection-local temporary tables, without permanent test rows or SMTP sends. It exercises production transactional registration and recipient/queue checks, including reused subscriptions, changed company addresses, invalid/missing mailboxes, isolation and no historical backfill. The queue test mirrors only its existing-delivery lookup into a temporary snapshot because MySQL cannot reopen the same temporary table for INSERT/SELECT; the production deduplication constraint and recipient selection remain in use. The existing `--rf-log-integration` suite also passed. Production deployment and the real phone drive-end-to-inbox flow remain separate acceptance steps.

Regression command:

```powershell
dotnet build CallAnalyzerRegression/CallAnalyzerRegression.csproj -p:OutputPath=buildcheck_rf_logs/ -p:UseAppHost=false
dotnet CallAnalyzerRegression/buildcheck_rf_logs/CallAnalyzerRegression.dll --rf-log-integration
```

Regression tests use in-memory ownership/event/storage/mail fakes plus real local HTTP routes, AWS SDK presigning, the actual existing PDF generator and a loopback-only SMTP capture server. They cover credential separation, login-derived user/company, CSRF, scope rejection, repeated registration/completion, immutable snapshot selection, regional isolation, private download authorization, a ZIP-to-PDF worker round trip, admin-only recipient registration, PDF MIME attachments, recipient-specific retries and unsubscribe cancellation. They do not send external emails or replace a live Ace Cloud/SMTP round trip and the final deployed acceptance test with real login/session data.

The explicit `--rf-log-email-db` regression mode exercises real MySQL registration, leases and retries using temporary UUID fixtures, and removes its own rows. It refuses an enabled integration or existing email/subscription data. It is for isolated setup validation, not a command to run against an active reporting system.
