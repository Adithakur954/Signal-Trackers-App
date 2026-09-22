# Deployment and final verification gates

These changes have not been deployed. Use synthetic staging data and approved scope for the tests below. No live database schema changes or credential rotation were executed by this task.

## Required configuration

| Setting | Requirement |
|---|---|
| ConnectionStrings__MySqlConnection / MySqlConnection2 | Rotated regional DB credentials from deployment secret storage; source values are now blank |
| ConnectionStrings__Redis | Redis credentials from secret storage; Redis is required for production login/session validation |
| SMS_API_KEY | Rotated SMS credential from secret storage |
| PythonBridge__ApiKey | Rotated bridge credential; use this .NET configuration key (a blank source key can shadow the legacy PYTHON_BRIDGE_API_KEY fallback) |
| Security__PublicApiKey | Configure for production mobile/health clients and rotate if exposed |
| Security__SessionIdleMinutes | Default 30; accepted range 5?120 minutes |
| Security__PersistentSessionDays | Absolute lifetime default 1; accepted range 1?30 days |
| Security__PasswordResetBaseUrl | Explicit approved HTTPS origin/base URL serving the reset flow; request Host is no longer trusted |
| Security__RequireCsrfHeader | Set true in production; current custom middleware remains until the reviewed CSRF proposal is approved |
| DATAPROTECTION_KEYS_PATH | Stable, access-restricted key storage shared as required across instances |
| Security__AllowedOrigins / forwarded proxy allowlist | Actual frontend origins and trusted proxy addresses; verify HTTPS/cross-site cookie behavior |

`appsettings.Production.example.json` is an example and is not automatically loaded. Configure the real production environment. Existing local development secrets were preserved under this user's .NET User Secrets location; they are not a production deployment mechanism.

## Expected changes requiring rollout coordination

- All users sign in again: previous tickets lack new credential/absolute-start properties, and login-lock keys are now regional v2 keys.
- Production login and protected requests require healthy Redis and readable regional user data. Verify graceful outage handling and recovery; per-request account checks add database load.
- Company admins create ordinary users; administrator creation and inspected global operations require super-admin. Verify UI controls reflect these permissions.
- Indoor plans without creator attribution become super-admin-only. Assign verified creators to legacy rows; do not guess ownership.
- Ordinary-user download links must exactly match Download_path on an accessible project. Associate legacy legitimate links explicitly.
- Password-reset links now require configured HTTPS base URL; old unsigned links expire immediately under the new verifier. Verify the actual email/frontend reset flow.
- Apply reviewed schema prerequisites ahead of rollout. Automatic production startup DDL is disabled; service-level Ensure* calls still require review.
- If the CSRF proposal is approved, implement its documented frontend token-fetch flow and POST legacy logout before rollout.

## Staging acceptance

1. Verify main/TW identities with overlapping IDs cannot change region or reach the other company's projects, plans, site rows, mutations, uploads and downloads.
2. Test every changed role boundary for ordinary user/company admin/super-admin, including forged role creation and edits of administrator accounts.
3. Obtain real cookies; replay after logout, password reset, deactivation, role/company change, idle expiry and absolute expiry. Test Redis/DB outages and multi-instance behavior.
4. Test configured reverse proxy and HTTPS, Secure/HttpOnly/SameSite flags, CSRF/CORS, upload/report/settings flows, reset email and new-login behavior.
5. Verify malformed/oversized CSV and ZIP input, authorized large datasets, canceled uploads, cleanup, transfer caps, disallowed redirects and timeout behavior.
6. Complete static/secret/dependency scans, remaining endpoint review, authenticated dynamic testing and independent VAPT. Record build identifier, evidence, owners and accepted residual risks.

Do not mark readiness complete until these gates and the pending approvals are resolved.
