# VAPT Readiness Notes

This project keeps production secrets outside source-controlled JSON files.

Required production values should be supplied through environment variables or a secret manager:

- `ConnectionStrings__MySqlConnection`
- `ConnectionStrings__MySqlConnection2`
- `ConnectionStrings__Redis`
- `SMS_API_ENDPOINT_URL`
- `SMS_API_KEY`
- `SMS_SENDER_ID`
- `SMS_TEMPLATE_ID`
- `SMS_ENTITY_ID`
- `DATAPROTECTION_KEYS_PATH`
- `ALLOWED_ORIGINS`
- `Security__RequireCsrfHeader=true` for production cookie-auth deployments

Security-sensitive startup code is organized under:

- `Configuration/` for service registration and hosting configuration.
- `Middleware/` for response security headers and cookie response handling.
- `Security/` for request and cookie security helpers.

Before production deployment:

- Rotate any credentials that were previously committed to `appsettings.json`.
- Keep `Security:AllowNullOrigin` set to `false`.
- Keep `Security:AllowLoopbackOrigins` set to `false` outside development.
- Keep `Security:RequireCsrfHeader` set to `true` in production and send the `X-CSRF-TOKEN` header with authenticated write requests.
- Store Data Protection keys outside the application source folder.
- Do not publish local upload folders, build outputs, logs, or database record dumps.
- Rotate any database, Redis, or SMS credentials that were ever present in repository history before VAPT.
