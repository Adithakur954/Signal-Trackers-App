# Dedicated RF report sender mailbox

## Current Gmail configuration

The selected sender is `vinfocom.client@gmail.com`, using `smtp.gmail.com`, port `587`, and required STARTTLS. On 26 October 2026 these settings and the supplied password were saved in the existing Development user-secret store for both `RfLogIntegration:Smtp:IN` and `RfLogIntegration:Smtp:TW`. No password is stored in this document or repository configuration. Regional recipients, reports and delivery records remain separate.

Live verification on 26 October 2026 passed a certificate-verified STARTTLS connection on port `587` and SMTP LOGIN authentication after the replacement App Password was saved for both regions. The earlier password had been rejected with status `535`. The SMTP username remains the full Gmail address, not the app name `Stracer`. See [Google's App Password instructions](https://support.google.com/accounts/answer/185833?hl=en) for account setup.

An explicitly authorized test email was then accepted by Gmail SMTP for the user-specified test recipient. Its subject was `[TEST] Stracer RF report - PDF delivery verification` and it attached `Stracer-RF-Test-Report.pdf` (142,414 bytes), generated from synthetic RF CSV data using the existing ZIP report engine. MailKit used the configured Gmail credentials and required STARTTLS. The test did not register a permanent recipient, write regional job records or upload S3 objects. SMTP acceptance is verified; inbox receipt and opening the PDF still require recipient confirmation. The ignored local test artifact directory contains the PDF and SMTP acceptance receipt, without credentials. This isolated test does not replace the deployed upload-complete/worker/recipient end-to-end acceptance test.

Development secrets do not configure IIS. After deployment, run the helper below on the server and enter the valid App Password at its private prompt. Repeat with `-Region TW` for Taiwan:

```powershell
.\scripts\Configure-RfLogReportEmail.ps1 -BackendPath 'C:\inetpub\wwwroot\Backend' -Region IN -SmtpHost 'smtp.gmail.com' -Port 587 -FromAddress 'vinfocom.client@gmail.com' -UserName 'vinfocom.client@gmail.com'
```

## Earlier company-mailbox proposal

The earlier proposed sender `rf-reports@vinfocom.co.in` has not been created. The GoDaddy instructions below apply only if a company-domain mailbox is chosen later.

## Company mail provider

Public DNS checked on 26 October 2026 returned these MX records for `vinfocom.co.in`:

| Priority | Mail server |
| --- | --- |
| 0 | smtp.secureserver.net |
| 10 | mailstore1.secureserver.net |

These match [GoDaddy's documented Professional Email/Titan MX records](https://www.godaddy.com/en-in/help/what-are-my-dns-settings-for-professional-email-powered-by-titan-42767). This identifies a likely provider; confirm the exact email product and available mailbox seats in the company's dashboard. No DNS records were changed.

## Create the dedicated mailbox

A company mail administrator must complete this step in the authenticated GoDaddy account. No GoDaddy admin connection is currently available in this workspace; there was no mailbox-management plugin returned for GoDaddy.

1. Open the company's GoDaddy **Email & Office Dashboard**.
2. Select **Add user** and the existing Professional Email/Titan product for `vinfocom.co.in`.
3. Enter `rf-reports` as the email username and `RF Reports` as the display name.
4. Set a unique mailbox password privately. This is the mailbox's password, separate from the GoDaddy account password.
5. Supply the company's approved recovery/account-contact email and create the mailbox using an available mailbox seat. If a purchase is required, the account owner must review the actual plan/price before proceeding.
6. Confirm the mailbox can sign in to webmail. Keep its password in the company's approved secret store; never paste it into chat or commit it.

Follow [GoDaddy's current account-creation guide](https://www.godaddy.com/en-in/help/create-my-professional-email-account-32289) for the dashboard's exact labels. The existing domain's mail routing stays in place; creating this sender does not require moving company email to a new provider.

## Connect the backend

For GoDaddy Professional Email/Titan, the [documented outgoing server settings](https://www.godaddy.com/en-in/help/use-imap-settings-to-add-my-professional-email-to-a-client-32204) are:

| Setting | Value after the mailbox is created |
| --- | --- |
| SMTP host | smtpout.secureserver.net |
| Port | 465 |
| Encryption | SSL/TLS immediately on connection |
| Username | rf-reports@vinfocom.co.in |
| From address | rf-reports@vinfocom.co.in |
| Password | The new mailbox's privately stored password |

The RF email sender supports both immediate TLS on port 465 and required STARTTLS on other TLS-enabled ports, such as 587. It validates server certificates normally and does not downgrade a TLS-enabled connection to plaintext.

After deploying the updated backend, run in administrator PowerShell on the IIS server:

```powershell
.\scripts\Configure-RfLogReportEmail.ps1 -BackendPath 'C:\inetpub\wwwroot\Backend' -Region IN -SmtpHost 'smtpout.secureserver.net' -Port 465 -FromAddress 'rf-reports@vinfocom.co.in' -UserName 'rf-reports@vinfocom.co.in'
```

The script prompts privately for the mailbox password. Run it again with `-Region TW` to explicitly configure Taiwan. The same authorized sending mailbox may be used for both regions, while subscriptions, reports and delivery records remain in their respective regional databases. Protect the deployed web.config with administrator/app-pool permissions.

The valid regional company profile email (`tbl_company.email`) is now automatically subscribed when a drive ZIP is registered. Use the company-admin recipient API only for optional additional recipients before registering the upload. Use a real approved company mailbox for the deployed acceptance test. SMTP acceptance is recorded as `sent`; inbox receipt and spam placement must also be checked by the recipient. Provider sending quotas still apply.

An S3 account and an email mailbox are separate services. The S3 access/secret keys cannot be used as SMTP credentials. Live verification still requires the new mailbox, backend server access and working Ace Cloud configuration.
