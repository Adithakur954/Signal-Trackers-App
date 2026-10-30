# STracer automatic RF authorization: trusted mobile-login integration

30 October 2026 | Backend implementation and app/login-server handoff

## Status and required change

The RF backend now implements `POST /api/rf-logs/mobile-enrollments/trusted`. It automatically authorizes an installation when a configured login server signs a short-lived assertion binding the verified user/company/region to that installation's enrollment ID and secret hash. The existing token endpoint then issues the RF bearer directly to the app. No administrator approval popup or manually shared token is required **for this verified path**.

This integration is disabled by default. It is not a production deployment or a replacement for user/device verification. The external `/MapView/user_signup_v2` implementation is not present in this repository. The developer's supplied "S-Tracer Mobile Login and Device Verification API" PDF says it currently authenticates neither the caller nor the device. Its existing success response, a numeric user ID, a matching mobile/company code or `ANDROID_ID` alone cannot authorize this new path. Simply signing that existing anonymous success response would preserve the same account-impersonation problem.

The login backend must establish trusted ownership/device verification before signing. That can use an already provisioned device credential and server challenge, or another agreed authenticated mechanism. A device ID string or knowledge of a phone number is insufficient. If no trusted mechanism exists, its design/implementation is an external prerequisite; this RF change cannot create proof of ownership from anonymous identifiers. An invisible automatic flow is possible only when the installation already has a trustworthy credential/verification mechanism. The RF backend does not add OTP or change the portal's email/password login.

## Responsibilities

| Owner | Required work |
| --- | --- |
| Mobile-login backend owner | Implement trusted verification, enforce active account/company/license and authoritative IN/TW mapping, issue the installation-bound signed assertion, protect the private signing key. |
| RF backend operator | Deploy this code, retain migrations 003–007 in both regions, configure the approved issuer/public keys and explicit regional company allowlists, enable only after acceptance. |
| Android developer | Persist installation identity securely, provide its UUID/proof hash to the login server, receive the assertion, call trusted enrollment, retrieve/store the RF token and upload ZIPs. |

No new database migration is needed for this endpoint. It uses migration 007's enrollment rows and migration 006's existing upload credentials. Existing manual approval and manual credential APIs continue working with their current authorization.

## Complete flow

1. The user enters the existing mobile number and company code. The app generates/persists a random enrollment UUID and 32 random bytes encoded as 64 lowercase hex characters, using Android Keystore-backed encrypted storage. Reuse these for retries and bind them to this account/region. Do not use `ANDROID_ID` as the secret.
2. Compute `proofHash = SHA256(UTF8(enrollmentSecret))`, encoded as uppercase hex. This hashes the **64-character secret string**, not the decoded 32 bytes.
3. The app supplies `enrollmentId` and `proofHash` to the login backend through its agreed upgraded login/device-verification contract. The exact upstream route/body is owned by that backend; no new `/MapView` route is created here. The login backend must validate the caller's trusted device/account proof and bind these fields to that verified interaction, rather than accepting them as account authentication.
4. After verification, the login backend returns a short-lived `rfLoginAssertion` alongside the existing user metadata. This field is the agreed proposed addition to its response; the existing server does not return it yet.
5. The app forwards the assertion and its enrollment secret to the RF trusted endpoint below. On `200` with `status: approved`, proceed directly; no approval screen is shown.
6. Generate/persist a random renewal UUID. Call the existing token endpoint with `previousRenewalId: null` for a new enrollment, or the stored renewal state when retrying an existing enrollment. Store the returned RF token securely.
7. Upload the final ZIP with `Authorization: Bearer <uploadToken>`. The existing storage, PDF and registered company-email pipeline continues unchanged.
8. Renew RF tokens using the existing enrollment secret/token endpoint. A new assertion is not required on every upload or renewal. Access tokens normally last 30 days; the existing enrollment authorization limit is 365 days. When a new enrollment is necessary, obtain fresh trusted verification and a new assertion.

Never substitute an unverified fallback when trusted verification fails. An older app using the original registration route will still see `pending`; it must implement the trusted route to use the new mode. The app must not silently re-enroll a denied/revoked installation to circumvent an administrator's decision. A new ID is a separate installation, so the login server must apply its own device revocation/issuance policy before signing it.

## RF enrollment request

```http
POST https://s-traccceer.vinfocom.co.in/api/rf-logs/mobile-enrollments/trusted
Content-Type: application/json
X-RF-Enrollment-Secret: <installation's 64 lowercase hex characters>
X-RF-Login-Assertion: <rfl1 signed assertion>
```

```json
{
  "enrollmentId": "<persisted installation UUID>",
  "region": "IN",
  "userId": 338,
  "mobileNumber": "<registered mobile>",
  "companyCode": "<typed company code>",
  "deviceLabel": "<recognizable phone label>"
}
```

The numeric user ID is illustrative. Use the login server's authoritative identity, not a hardcoded ID. `companyCode` means the typed company code; the assertion's `companyId` is the regional database company ID. They are different fields. Preserve the exact installation ID, proof and registration details across retries.

Success is `200`, containing the existing enrollment metadata with `status: "approved"`, `enrollmentId`, `region`, `userId`, `companyId`, approval dates and renewal state. It deliberately contains no RF bearer yet. Responses use `Cache-Control: no-store`.

```http
POST /api/rf-logs/mobile-enrollments/<enrollmentId>/token
Content-Type: application/json
X-RF-Enrollment-Secret: <saved installation secret>
```

```json
{"region":"IN","renewalId":"<persisted renewal UUID>","previousRenewalId":null}
```

The `200` response contains `tokenType: "Bearer"`, `uploadToken`, credential metadata, expiry and renewal state. Use the existing [renewal/retry contract](rf-mobile-auto-enrollment.md#6-retrieve-and-renew-the-upload-token-automatically); repeat the same renewal ID after a lost response and serialize rotations. Trusted enrollment retries do not rotate tokens, extend approval or clear revocation.

```http
POST /api/rf-logs/upload
Authorization: Bearer <uploadToken>
Content-Type: multipart/form-data; boundary=<generated by HTTP client>
```

Multipart fields: `file`, stable per-ZIP UUID `uploadId`, valid owned `sessionId`; omit unknown optional `projectId`/`subSessionId`, rather than sending `0`. Existing ownership checks still apply. Do not send the enrollment secret or login assertion with ZIP uploads. Native bearer requests do not use portal cookies or CSRF headers.

## Exact signed-assertion contract: login-server owner

This is a constrained RSA-signed envelope, **not a JWT**. There is no selectable algorithm or token-provided signing-key URL. Only configured public keys are trusted.

```text
assertion = rfl1.<keyId>.<payloadBase64Url>.<signatureBase64Url>
signedBytes = ASCII("rfl1." + keyId + "." + payloadBase64Url)
signature = RSA-SHA256-PKCS1-v1_5(privateKey, signedBytes)
```

Use RSA of at least 2048 bits. Base64url encoding is unpadded and canonical. `keyId` is 1–64 ASCII letters/digits/underscore/hyphen. Sign the exact bytes and forward them unchanged; do not decode/re-serialize the JSON in the app. Payload is UTF-8 JSON, at most 4096 bytes; the complete assertion is at most 8192 characters. Duplicate/unknown JSON members are rejected.

Example payload shape (replace all illustrative values and calculate actual timestamps):

```json
{
  "issuer": "<configured login-server issuer>",
  "audience": "stracer-rf-enrollment",
  "issuedAt": 1793318400,
  "expiresAt": 1793318490,
  "enrollmentId": "<same installation UUID as the request>",
  "region": "IN",
  "userId": 338,
  "companyId": 4,
  "mobileNumber": "<authoritatively registered mobile>",
  "companyCode": "<authoritatively registered company code>",
  "proofHash": "<64 uppercase hex SHA-256 of UTF-8 enrollment secret string>"
}
```

`issuedAt`/`expiresAt` are integer Unix UTC seconds. Lifetime must be positive and at most 120 seconds; 90 seconds is suggested. Expired assertions are rejected without an expiry grace period. `issuedAt` may be at most 5 seconds in the future and cannot be more than 120 seconds old. Keep both servers' clocks synchronized. The issuer/audience must match configuration exactly. Region must match the signing key's fixed region and request region. Company ID must belong to the key's explicit allowlist. Request identity fields and the proof hash must match the signed fields, and the current RF database must still associate that user/mobile/code with the asserted company.

The payload/signature is signed, not encrypted. It contains personal identifiers; do not log or put it in URLs. Capturing the assertion alone does not authorize a different installation: it is bound to an exact ID and installation secret hash. Replays for the same installation are idempotent within its short assertion lifetime; this is not a globally single-use assertion. The database transaction cannot revive denied, revoked, inactive or expired enrollments. No arbitrary signing secret belongs in the APK, and no common upload token is shipped with it.

### Signing example for the login server (C#)

```csharp
// Execute ONLY after trusted verification and authoritative identity resolution.
// privateKey is loaded from the login server's protected secret store.
var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload); // exact camelCase fields above
var payload64 = WebEncoders.Base64UrlEncode(payloadBytes);
var unsigned = $"rfl1.{keyId}.{payload64}";
var signature = privateKey.SignData(Encoding.ASCII.GetBytes(unsigned),
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var rfLoginAssertion = unsigned + "." + WebEncoders.Base64UrlEncode(signature);
```

Required namespaces: `System.Text`, `System.Text.Json`, `System.Security.Cryptography`, `Microsoft.AspNetCore.WebUtilities`. The login server chooses its implementation language; the signature/encoding contract must match. Use a new dedicated signing key, not an S3 key, SMTP password, reporting API key or portal password. Retain a secure private-key backup/rotation procedure on the issuer side.

## RF backend configuration and rollout

Deploy the backend normally with existing production S3, SMTP, reporting and regional settings preserved. Do not run a storage reconfiguration just to enable this feature. The following is a configuration **template**, not live values:

```json
{
  "RfLogIntegration": {
    "TrustedMobileLogin": {
      "Enabled": false,
      "Issuer": "<agreed issuer>",
      "Audience": "stracer-rf-enrollment",
      "Keys": [
        {
          "KeyId": "login-in-v1",
          "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n<SPKI public key body>\n-----END PUBLIC KEY-----",
          "Region": "IN",
          "CompanyIds": [4]
        }
      ]
    }
  }
}
```

Use actual authorized regional database company IDs, not the example ID. Add separate, uniquely named keys/entries for Taiwan and other authorized scopes. Empty company lists trust no company. Duplicate key IDs are rejected; no token-supplied key can expand scope. Only SPKI `BEGIN PUBLIC KEY` PEM is accepted at the RF server; do not install the private key here.

For IIS environment configuration the prefix is `RfLogIntegration__TrustedMobileLogin__`. Examples: `Enabled`, `Issuer`, `Audience`, `Keys__0__KeyId`, `Keys__0__PublicKeyPem`, `Keys__0__Region`, `Keys__0__CompanyIds__0`. PEM values must contain actual line breaks, not literal backslash-n from shell quoting. `appsettings.Production.example.json` only documents settings; it is not automatically loaded. Preserve live `web.config` environment overrides across releases.

Enable `TrustedMobileLogin:Enabled` only after the login service's verification/signing work and the app forwarding work have passed staging acceptance. Do not enable it merely after copying a public key. For rotation, introduce a new key ID/public key, switch the issuer, wait out outstanding assertion lifetimes and remove the old public key. Removing a key blocks new enrollments through it; already-issued RF tokens/enrollments retain their existing lifetime until separately revoked. Disabling this integration also blocks new trusted enrollments, not existing RF bearer use/renewal.

## Errors and acceptance

| Result | Meaning and action |
| --- | --- |
| `200 approved` | Continue automatically to the existing token endpoint; no approval popup. |
| `400` | Invalid registration/secret/UUID/body; correct fields. |
| `401` | Missing, forged, mismatched or expired assertion, or revoked installation. Refresh verified login once for assertion expiry; do not loop on revocation. |
| `403` | User/company ownership no longer active/matching. Resolve the authoritative association. |
| `404` | Existing ID/secret mismatch. Never overwrite saved installation proof or take over another ID. |
| `409` | ID/details conflict, denied/expired enrollment. Respect denial; a fresh verified enrollment requires issuer policy approval. |
| `429` | Existing pending limits or request rate limit; reuse IDs and back off. |
| `503` | Integration disabled or public-key/issuer configuration unavailable. Coordinate server rollout; do not fall back to unverified upload. |

Local regressions cover disabled mode, wrong signer, tampered assertion, audience/issuer, time limits, wrong user/company/region/ID/proof, IN/TW key scope, inactive owners, replay idempotency, revoked/denied installation protection and HTTP enrollment → token → ZIP upload. They use disposable in-memory stores and a test signing key; they do not send production emails or validate the external login implementation.

Run from the backend repository:

```powershell
dotnet build CallAnalyzerRegression/CallAnalyzerRegression.csproj -c Release
dotnet run --project CallAnalyzerRegression -c Release --no-build -- --rf-mobile-upload
dotnet run --project CallAnalyzerRegression -c Release --no-build -- --rf-log-integration
```

Production acceptance still requires: trusted verification demonstrated at the login server; unauthorized callers unable to obtain assertions; real app automatic enrollment with no manual popup; correct session ownership; private Ace Cloud upload; PDF generation; delivery to the approved company recipient; revocation and failure-path checks. The portal's email/password flow is unchanged by this integration.
