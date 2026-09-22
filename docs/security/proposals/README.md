# Review-only security proposals

These patches have **not** been applied. Automatic approval review rejected the runtime changes because compatibility and exception coverage were not sufficiently verified. The current build and test results do not cover these proposed implementations. Both patches were generated from the same current working tree; integrate their overlapping Auth/Home changes together, not by blindly applying one after the other.

## Default authentication

`default-auth.patch` enables a fallback authenticated-user policy and explicitly preserves the current public login/recovery/status actions, API-key Python bridge, and OTP/deletion-token flows. Home logout and logged-user lookup become explicitly authenticated. Verify all 52 descriptors currently missing standard authentication metadata against this disposition before applying. PublicApiKey-protected mobile/health routes already declare AllowAnonymous and retain their custom filter.

Acceptance tests: unauthenticated browser data routes return 401; all intended login/recovery/health/OTP/API-key contracts still behave correctly; absent/invalid bridge and mobile keys remain denied; a newly added endpoint defaults to authentication.

## CSRF migration

`csrf-migration.patch` uses ASP.NET antiforgery instead of the current unsigned cookie/header equality comparison. Production defaults to validation; existing explicit development opt-out remains. Browser integration changes are required:

1. Fetch GET `/api/auth/csrf` with `credentials: include` before login.
2. Read `requestToken` from JSON and send it in `X-CSRF-TOKEN` with login and subsequent POST/PUT/PATCH/DELETE requests, also including credentials.
3. Fetch a new token immediately after login, logout, or switching users. Tokens are bound to the current identity.
4. Keep the antiforgery cookie HTTP-only; do not read it in JavaScript. Refresh the token on a CSRF rejection, avoiding automatic retries of non-idempotent writes.
5. Change legacy GET `/Home/Logout` calls to POST.

Acceptance tests: missing/wrong/mismatched-user tokens fail; a valid token works for login and signed-in writes; allowed-origin cross-site cookies work; unexpected origins cannot read tokens; API-key mobile requests retain their independent authentication; frontend upload/report/settings/logout workflows pass.

Deployment requires the actual frontend repository/environment and test accounts. No deployment, application restart, or external active testing is authorized by these patch files alone.
