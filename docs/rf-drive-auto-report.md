# Automatic drive PDF and company email

When a user finishes a drive, the app uploads the final RF log ZIP and notifies the backend. The backend automatically generates the PDF with the existing ZIP-to-PDF engine and emails the PDF attachment to that user's company's registered email (`tbl_company.email`). No manual PDF generation or send action is required.

## App developer

Using the existing authenticated user session, automate these steps after drive completion:

1. `POST /api/rf-logs/uploads` with a stable UUID for the final ZIP, its filename/size and the real session ID (optional sub-session/project IDs).
2. `PUT` the raw ZIP bytes to the response's signed `uploadUrl`, with `Content-Type: application/zip`.
3. After successful PUT, `POST /api/rf-logs/uploads/{uploadId}/complete` with no body.

Preserve the upload UUID and original metadata across retries. Registration and completion are idempotent; don't create a new UUID for a retry. Submit the final drive ZIP once, rather than registering multiple partial ZIPs for one report. Upload failures must not call complete until corrected.

Optional progress: `GET /api/rf-logs/uploads/{uploadId}`. `reportStatus=completed` means PDF generation finished; email status is tracked separately. The app never needs SMTP credentials, a recipient address, S3 credentials or a report-generation API call. It must still implement this drive-end upload sequence; backend changes alone cannot trigger phone-side code.

## Backend and destination

- Company and region come from the verified login/session ownership. IN reads India DB; TW reads Taiwan DB. No cross-region fallback.
- At ZIP registration, the backend reads the valid company profile email and creates/reuses its company-managed report subscription transactionally. A matching optional recipient is reused, so the same email has one delivery per upload.
- The existing background report worker processes completed uploads, stores the private PDF and queues email with the PDF attached. SMTP failures use the existing durable retries and do not regenerate the PDF.
- The sender account is the configured regional SMTP account. The destination is the company's registered email; it is not automatically the uploader's login email or the SMTP sender.
- Before sending a company-managed report, the current company profile email is checked again. Changing/deleting the profile prevents sending pending reports to the former address. The replacement receives new drives, not historical reports.
- A missing/invalid company email allows ZIP/PDF processing but creates no automatic company delivery. Correct the profile before registering the next new drive ZIP. Optional approved recipients continue to work.

## Frontend developer

Company email needs no separate RF subscription form. The recipient API returns `companyDefault: true` for the automatically managed address after an upload registers it. Show it as company-managed. DELETE on an enabled company-managed recipient returns 409; update the company profile email to change the destination.

Existing `POST /api/rf-logs/recipients` remains available for optional additional recipients registered before the upload. Admin monitoring uses `GET /api/rf-logs/email-deliveries?uploadId={uploadId}` and the existing failed-delivery retry route. `sent` means SMTP accepted the message; inbox receipt still needs confirmation.

## Deployment

Deploy this backend and run `dotnet .\SignalTracker.dll --rf-log-db-migrate --environment Production` to apply/verify migrations 003/004/005 in both regional DBs. This includes the new `company_default` column; no recipient backfill or email sending occurs during migration. Configure server-side S3 and regional SMTP settings, enable RF integration, keep built-in reporting/email workers enabled and IIS running.

Validate with a real IN account and independently a TW account: final ZIP upload, complete, generated PDF, matching company email delivery, confirmed inbox/PDF receipt. Existing phone integration and production deployment must be completed for this workflow to run live.
