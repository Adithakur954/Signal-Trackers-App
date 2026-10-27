SignalTracker RF ZIP integration - IIS release

This package preserves the server's existing appsettings*.json, web.config,
App_Data, logs and UploadedExcels. No storage or database credentials are included.
Both regional DB connection strings must already be configured on the server:
  MySqlConnection = India; MySqlConnection2 = Taiwan (no fallback).

1. Extract this ZIP to a separate folder on the deployment server.
2. IIS Manager > Sites > Backend > Basic Settings: note the Application Pool name.
3. Open PowerShell as Administrator in the extracted package folder.
4. Run (replace YOUR_ACTUAL_POOL_NAME with the name from step 2):

   .\scripts\Deploy-RfLogIntegration.ps1 -PackagePath .\backend -BackendPath 'C:\inetpub\wwwroot\Backend' -AppPoolName 'YOUR_ACTUAL_POOL_NAME' -ConfigureStorage

   The script privately prompts for NEW rotated Ace Cloud S3 credentials.
   Signing region is ap-south-noi-1, confirmed in this bucket's configuration screen.
   For a different endpoint, pass its actual region with -SigningRegion.
   Credentials from chat must be rotated before use. Do not paste replacement secrets in chat.

The script backs up live configuration (not old binaries), stops only the selected
pool, copies the release, creates/grants access to writable folders, applies and
checks the RF table in BOTH databases, checks a real S3 upload/copy/download/report
round trip, then starts the pool. A failed check leaves the pool stopped; correct
the stated issue and rerun. Check objects are created and deleted only under
rf-logs/checks/<random-id>/; allow DeleteObject on that check prefix.

5. Open the backend site once. Automatic PDF/email workers run independently for IN/TW.
   Set IIS Application Initialization/preload for automatic startup after server
   restart. The script disables idle timeout and sets the pool to AlwaysRunning.
6. Before a drive upload, a company admin registers report email recipients:
     POST /api/rf-logs/recipients
     {"email":"rf-team@example.com","name":"RF reporting team"}
   Use the existing admin login cookie and CSRF header. Company/region come from
   verified login claims. Only this explicit list receives future company reports.
   Configure SMTP separately for IN and TW. In the extracted package:
     .\scripts\Configure-RfLogReportEmail.ps1 -BackendPath 'C:\inetpub\wwwroot\Backend' -Region IN -SmtpHost 'smtp.YOUR-PROVIDER.com' -Port 587 -FromAddress 'reports@YOUR-DOMAIN.com' -UserName 'reports@YOUR-DOMAIN.com'
   Enter the SMTP app password privately when prompted; repeat with -Region TW.
   For the proposed company mailbox/GoDaddy setup, see docs/rf-report-mailbox-setup.md.
   GoDaddy Professional Email uses smtpout.secureserver.net, port 465 with TLS.
   The RF sender supports immediate TLS on 465 and required STARTTLS on 587.
   Alternatively, existing active m_email_setting settings in EACH DB are reused.
   The PDF is emailed as an attachment (default cap 20 MiB). See docs for delivery
   diagnostics, unsubscribe and per-recipient retries. No SMTP credentials are in this package.
7. Give the app team these two endpoints on the deployed HTTPS origin:
     POST /api/rf-logs/uploads
     POST /api/rf-logs/uploads/{uploadId}/complete
   Existing email login determines user/company/region. See docs for PUT sequence.
8. Poll GET /api/rf-logs/uploads/{uploadId}; when reportStatus=completed, use
   GET /api/rf-logs/uploads/{uploadId}/report for a short-lived PDF download link.

By default the backend generates the PDF with the existing ZIP report engine.
No separate reporting URL is required. External reporting is an optional mode;
see docs/rf-log-integration.md and the configure helper's -ExternalReporting option.

Live acceptance test: log in, register a ZIP belonging to a real session, PUT it
using the returned URL, call complete, wait for completed and download the PDF.
Repeat with a Taiwan login. Each event/report/subscription stays in its own regional DB.
Verify email delivery to a recipient registered before the upload. No real test
recipient is installed automatically and no real email is sent by the build tests.
Browser uploads also require bucket CORS allowing the actual frontend origin.

The build's automated checks use real HTTP routes and the PDF generator with
fake event/storage services; the server storage round trip tests the actual bucket.
