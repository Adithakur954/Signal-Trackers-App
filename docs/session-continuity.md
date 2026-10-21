# Portal Session Continuity

`Security:KeepSignedInUntilLogout` defaults to `true`. Sign-in creates a persistent
365-day cookie, renewed during authenticated use. The previous idle timeout and
one-day absolute cutoff do not apply in this mode. A browser that stays unused
for a full year, deletes its cookies, or loses access to the original protection
keys still requires sign-in. Set the flag to `false` to restore configured idle
and absolute session limits.

The regional `tbl_login_lock` row is the authority for login ownership. Redis is
optional for session continuity. Normal duplicate sign-in retains the existing
conflict/confirmation flow; a successful forced sign-in replaces ownership and
invalidates the previous cookie on its next authenticated request.

Session renewal uses an atomic owner-checked update; successful requests do not
need a separate lock-read/transaction/commit sequence. Old renewals and old
logout requests cannot replace or delete a newer login. Explicit logout deletes
the matching DB row even when Redis is offline. Missing rows are revoked
sessions, never automatically recreated by old cookies.

Temporary database/verification outages return HTTP 503 with `retryable: true`
and `Retry-After: 5`, preserving the cookie without authorizing the request.
Disabled accounts, changed credentials/roles/company, revoked/expired licenses,
and confirmed replacement sessions continue to invalidate authentication.

Validated cookies also restore legacy session fields after an IIS worker recycle.
KeepAlive uses authenticated identity rather than the lifetime of RAM session data.

Deployment:

- Deploy and recycle the backend. Existing Redis-only sessions without a DB login
  row must sign in once. Expired/deleted cookies cannot be restored automatically.
- Keep `DataProtection:KeysPath` stable and outside files replaced by deployment.
  Preserve existing key files and give the IIS identity read/write access.
  Multiple backend instances must use the same key ring and application name.
- No frontend changes or credential changes are required.

Tests:

```text
dotnet build CallAnalyzerRegression/CallAnalyzerRegression.csproj -o artifacts/session-continuity-regression /p:UseAppHost=false
dotnet artifacts/session-continuity-regression/CallAnalyzerRegression.dll --session-continuity
```
