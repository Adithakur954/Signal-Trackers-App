# Android RF upload without portal login

The mobile user keeps the existing mobile-number/company-code entry flow. The Android app does **not** call `/api/auth/login`, send a portal password, or receive storage/SMTP keys for RF uploads. The upload authorization is a separate, administrator-provisioned credential for each app installation/user. This is now supported in backend source; the updated binaries and migration must be deployed before use.

## 1. Server deployment and one-time enrollment

1. Build/deploy the updated backend using the existing RF release process. Preserve live `web.config`, S3, SMTP and database settings.
2. Apply migration `006_rf_mobile_upload_credentials.sql` to **both** regional databases. The existing `dotnet SignalTracker.dll --rf-log-db-migrate --environment Production` command now includes it, and `--rf-log-db-check` checks the new table. Under IIS, run through `scripts/Deploy-RfLogIntegration.ps1`, which imports the site's environment variables for the CLI checks. Plain CLI commands do not automatically read IIS `web.config` variables.
3. Ensure the existing mobile user is active and belongs to the intended company in the correct regional database. The company code and mobile number must match the existing database records (surrounding whitespace is ignored). Provisioning does not create users or reassign company ownership.
4. A company administrator issues a distinct upload credential for each installation/user. The operator can use:

```powershell
.\scripts\New-RfMobileUploadCredential.ps1 `
  -MobileNumber '<registered mobile number>' `
  -CompanyCode '<registered company code>' `
  -DeviceLabel '<label for this app installation>' `
  -Region IN
```

The operator is prompted privately for **administrator portal credentials**. These are used only by the provisioning tool, not by mobile users or the Android app. Portal login/licensing and single-session rules still apply. The tool signs out after provisioning. Use `-UserId <existing user ID>` when multiple registered users match the same mobile/company. The credential defaults to 90 days; `-ValidDays` accepts 1–365. The raw token is shown once, so run without shell transcription/screensharing and transfer it privately to the intended app installation.

The equivalent administrator API is `POST /api/rf-logs/mobile-credentials` using the administrator's existing portal cookie and `X-CSRF-TOKEN` matching `st.csrf`:

```json
{
  "mobileNumber": "<registered mobile number>",
  "companyCode": "<registered company code>",
  "deviceLabel": "<installation label>",
  "validDays": 90
}
```

The response contains `credential`, `tokenType: "Bearer"` and `uploadToken`. Only a SHA-256 digest is stored. `GET /api/rf-logs/mobile-credentials` lists safe credential metadata for the administrator's company. `DELETE /api/rf-logs/mobile-credentials/{credentialId}` revokes a credential. Phone number/company code alone cannot call these APIs. They remain administrator-only and CSRF-protected. Existing reporting-service keys cannot provision credentials.

This enrollment step is required because the existing number/company-code entry does not prove phone ownership. No OTP or portal login is added to the mobile user journey. Provisioning is an operator step; implement a secure installation-specific setup/import mechanism in the app. A device label is metadata, not hardware attestation. The secret's possession authorizes the uploader: never hardcode one shared token in all APKs or expose an unauthenticated token lookup by phone/company. On reinstall, account/company switch or credential expiry, provision the appropriate replacement. Revoke old credentials promptly when a device is lost or access is removed.

## 2. What the Android developer implements

1. Add a secure one-time setup/import for the provided installation credential. Store it using Android Keystore-backed encrypted storage. Associate it with the provisioned user/company/region, not just the displayed phone number. Do not log it or include it in crash reports or backups.
2. Keep the existing phone/company-code entry screen. Do not implement portal email/password login for uploads. Do not allow an account switch to silently retain another user's upload identity.
3. After drive completion, finish the ZIP, then send one multipart request:

```http
POST https://s-traccceer.vinfocom.co.in/api/rf-logs/upload
Authorization: Bearer <installation uploadToken>
```

| Form field | Value |
| --- | --- |
| `file` | Final ZIP binary, e.g. `log_8332.zip` |
| `uploadId` | UUID generated once per ZIP and preserved across retries |
| `sessionId` | Existing drive-session ID owned by the provisioned user, e.g. `8332` |
| `projectId` | Optional positive project ID in the same company; omit if unavailable, do not send `0` |
| `subSessionId` | Optional actual sub-session identifier; omit if unavailable |

`userId` is derived from the credential and need not be sent. If sent, it must match that credential. Do not send `companyId`, recipient email or a region override to change scope: the credential fixes the user, company and IN/TW database. The backend validates that the drive session belongs to that user and the optional project belongs to that company.

Use a dedicated backend upload HTTP client without portal cookies. **No `Cookie` or `X-CSRF-TOKEN` header is required for this bearer-only flow.** Do not add fake CSRF values. The existing cookie-authenticated portal routes still require CSRF when enabled; a bearer header does not bypass CSRF validation if a valid portal cookie is also present. Let the HTTP client generate the multipart Content-Type/boundary. Send credentials only to the configured HTTPS backend and do not forward them to storage URLs or other origins.

4. On successful acceptance (`200`), retain the stable upload UUID. Repeating the same metadata under the same credential returns the existing event without another ZIP/report. Do not modify the ZIP under that UUID. Optional `GET /api/rf-logs/uploads/{uploadId}` and `GET /api/rf-logs/uploads/{uploadId}/report` accept the same bearer credential. Upload access is also bound to its original credential ID; do not switch installation credentials in the middle of a retry and assume ownership of a previous upload.
5. Treat `401` as a missing/invalid/expired/revoked credential requiring re-provisioning. Do not retry it indefinitely or fall back to anonymous upload. `403` means a user/session/project scope mismatch. `400` means invalid form data, `409` a metadata conflict, `413` the size cap, and `503` a server/storage/database availability or configuration issue. Retry transient failures with backoff and the same UUID.

## 3. Automatic reporting and email

An accepted ZIP uses the existing private S3 upload, verification, durable regional event queue, PDF worker and SMTP delivery worker. The automatic default recipient remains the company's registered **`tbl_company.email`**, with any eligible configured additional recipients. It is not the mobile number, the SMTP sender mailbox, or whichever portal account happens to be logged in. The app cannot choose an arbitrary recipient. No manual Generate PDF or Send Email call is needed.

The ZIP still needs the supported NetworkLog CSV; optional L3/Event/Wi-Fi companions remain supported as before. A Wi-Fi-only ZIP does not become a supported report format by adding authentication. `200` confirms upload acceptance, not successful PDF generation or inbox delivery. Report generation and delivery remain asynchronous and retain their existing retry/status behavior.

## 4. Validation performed

The mobile credential regression checks issuance scope, token tampering, region tampering, expiry, revocation and secret-safe responses. The RF HTTP regression checks administrator-only provisioning, cookieless upload/retry, user/ownership rejection before storage writes, role isolation and revocation. The existing RF PDF/email regressions exercise report generation, email attachment creation, tenant/region separation and retry deduplication using test stores and loopback SMTP. These are local regression results, not confirmation of deployment, execution of the new MySQL migration on production, Android acceptance, or delivery through the real SMTP account.

```powershell
dotnet run --project CallAnalyzerRegression -- --rf-mobile-upload
dotnet run --project CallAnalyzerRegression -- --rf-log-integration
```
