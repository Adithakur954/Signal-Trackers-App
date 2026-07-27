# Signal Trackers Backend Documentation

## 1. Backend Overview

Signal Trackers is an ASP.NET Core MVC/API backend using:

- ASP.NET Core controllers for web pages and JSON APIs.
- Entity Framework Core with MySQL.
- Cookie authentication plus server-side session.
- Redis for caching, login locking, and fast invalidation. If Redis is not configured, the app falls back where possible.
- CSV/ZIP upload processing for network logs, sessions, projects, polygons, and prediction data.
- A secured Python bridge API for external Python/ML/report jobs.

The application entry point is `Program.cs`.

## 2. Startup Flow

`Program.cs` performs the backend setup in this order:

1. Loads configuration.
   - `appsettings.json`
   - `appsettings.Development.json`
   - development user secrets from `SignalTracker.LocalDevelopment`
   - environment variables

2. Registers application services.
   - `UserScopeService`
   - `LicenseFeatureService`
   - `PythonBridgeService`
   - `OtpService`
   - `SmsService`
   - `UserDeletionService`
   - optional `UserDeletionCleanupService`

3. Configures MVC/API.
   - `AddControllersWithViews`
   - production exception response filter
   - JSON property names are preserved as written in C#.

4. Configures security.
   - CORS policy named `AllowReactApp`
   - data protection keys
   - cookie auth
   - session
   - forwarded headers
   - HTTPS redirection

5. Configures database.
   - `ApplicationDbContext` uses MySQL.
   - Connection string is selected dynamically by `DbConnectionProvider`.
   - Main DB uses `ConnectionStrings:MySqlConnection`.
   - TW/secondary DB uses `ConnectionStrings:MySqlConnection2`.

6. Configures Redis.
   - Uses `ConnectionStrings:Redis` if available.
   - Uses resilient Redis options.
   - If Redis fails or is not configured, Redis-dependent features degrade gracefully unless explicitly required.

7. Configures upload limits.
   - Default max upload comes from `Security:MaxUploadBytes`.
   - Hard cap is 500 MB.

8. Builds the middleware pipeline and maps controllers.

## 3. Middleware Request Flow

Every request passes through this pipeline:

1. Security headers middleware.
2. Production exception handler and HSTS, only outside development.
3. Forwarded headers.
4. HTTPS redirection when enabled.
5. Static file serving.
6. Legacy route rewrite:
   - `/api/MapView/GetSubSessionAnalyticsWithStatus`
   - rewritten to `/api/MapView/GetSubSessionAnalytics?includeStatus=1`
7. Routing.
8. CORS.
9. Cookie policy.
10. Session.
11. Session keep-alive pulse.
12. Cookie authentication.
13. CSRF protection middleware.
14. Authorization.
15. Partitioned cookie support.
16. Controller endpoint execution.

## 4. Authentication Flow

Main endpoint:

- `POST /api/auth/login`

Login flow:

1. Client sends email, password, optional `country_code`, and optional `ForceLogin`.
2. If `country_code = TW`, backend checks the TW database first.
3. Otherwise, backend checks the main DB first and can fall back to TW.
4. Password is verified by `PasswordSecurity`.
5. Legacy/plain text passwords can be accepted and upgraded to a secure hash.
6. Redis login lock is created per user:
   - key: `auth:login-lock:user:{userId}`
   - TTL: 18,000 seconds
7. If another login lock exists, login fails unless `ForceLogin = true`.
8. Cookie auth claims are created:
   - `UserId`
   - `UserTypeId`
   - `CompanyId`
   - `company_id`
   - `country_code`
   - email/name claims
9. Session values are also stored:
   - `country_code`
   - `UserName`
   - `UserID`
   - `UserType`
   - `CompanyId`
10. Response returns login status, source DB, user details, and enabled features.

Other auth endpoints:

- `POST /api/auth/logout`
- `GET /api/auth/status`

Logout clears:

- Redis login lock.
- Auth cookie.
- Server session.

## 5. Authorization and Scope Flow

Most backend APIs require cookie authentication through `[Authorize]`.

Public mobile/helper endpoints use:

- `[AllowAnonymous]`
- `[PublicApiKey]`

Those public endpoints require either:

- `X-Public-Api-Key`
- `X-API-Key`

User/company scoping is handled by `UserScopeService`.

Roles:

- `1` = normal user
- `2` = admin
- `3` = super admin

Scope behavior:

- Super admin can access global data by default.
- Super admin can scope to a requested company ID.
- Normal users/admins are restricted to their own `CompanyId`.
- Some endpoints fall back to current user scope when company scope is missing.

## 6. Dynamic Database Selection

`ApplicationDbContext` gets its connection string from `DbConnectionProvider`.

Selection rules:

- Login and important auth/user-management paths use main DB.
- If request has `country_code = TW`, use TW DB.
- Country can come from:
  - query string: `country_code`
  - header: `x-country-code`
  - auth claim: `country_code`
  - session: `country_code`
- Otherwise, use main DB.

Both MySQL connections are normalized to support zero dates:

- `Allow Zero DateTime = true`
- `Convert Zero DateTime = true`

Connection pool size is also constrained.

## 6.1 Country-Wise Database Model

This backend supports two MySQL databases:

| Logical database | Connection string key | When it is used |
|---|---|---|
| Main database | `ConnectionStrings:MySqlConnection` | Default DB for India/main users, auth entrypoints, most admin write paths, and any request without TW context. |
| TW/secondary database | `ConnectionStrings:MySqlConnection2` | Used when the request/user country context is `TW`. |

Country code is stored mainly on:

- `tbl_user.country_code`
- `tbl_company.country_code`
- auth claim: `country_code`
- session value: `country_code`

The database provider resolves country in this priority:

1. Query string: `?country_code=TW`
2. Header: `x-country-code: TW`
3. Auth claim from login cookie: `country_code`
4. Session value: `country_code`
5. Default: main DB

Example DB routing:

| Request/context | Selected DB |
|---|---|
| User logs in through `/api/auth/login` | Main DB first unless request asks for `country_code = TW`; can fallback to TW. |
| Logged-in user has claim `country_code = TW` | TW DB for normal scoped requests. |
| Logged-in user has claim `country_code = IN` or empty | Main DB. |
| API call has header `x-country-code: TW` | TW DB, if not overridden by a hardcoded main-DB path. |
| No login/session/country header | Main DB. |

Important exception:

- Login/auth endpoints and selected admin user-management write paths are pinned to the main DB to avoid cross-database write confusion.
- `AuthController` manually checks TW during login and password upgrade when needed.
- Data deletion OTP/token services can search both main and TW databases by phone/token.

### Country-Wise Login Flow

`POST /api/auth/login` has special logic:

1. If request sends `country_code = TW`, it checks TW DB first.
2. If request does not ask for TW, it checks main DB first.
3. If main DB user has `country_code = TW`, it then checks TW DB.
4. If main DB authentication fails, it can still fallback to TW DB.
5. After successful login, the backend stores `country_code` in both claims and session.
6. Later requests use that stored country code to pick the correct DB.

This means the selected DB after login is not random. It follows the authenticated user's country.

### Country-Wise Data Separation

The code treats main DB and TW DB as separate data stores. A TW user's sessions, logs, projects, settings, and company data are expected to live in the TW database. Main users' data lives in the main database.

Operational rule:

- If a user says "my data is missing," first check which country DB their login resolved to.
- Check `/api/auth/status` response and claims/session diagnostics.
- For admin troubleshooting, `/Admin/DbDiagnostics` is useful because it shows selected DB context information.

## 6.2 Company-Wise Data Model

Company ownership is the second major isolation layer inside a selected database.

Company fields:

- `tbl_company.id` is the company primary key.
- `tbl_user.company_id` links users to a company.
- `tbl_project.company_id` is used by many project/prediction APIs.
- Some older rows may rely on user ownership instead of direct company ID.

Role IDs:

| Role ID | Meaning | Scope behavior |
|---|---|---|
| `1` | Normal user | Restricted to own company/user data. |
| `2` | Admin | Restricted to own company data. |
| `3` | Super admin | Can access all companies or filter to a requested company. |

`UserScopeService` is the main company-scope resolver.

Scope rules:

| User type | Requested `company_id` | Effective scope |
|---|---|---|
| Super admin | not provided | global/all companies, represented as `0` |
| Super admin | provided | requested company ID |
| Admin/user | any value | user's own `CompanyId` claim/session |
| Admin/user | no company resolved | many endpoints return unauthorized or fallback to current user scope |

Company ID can come from:

- `CompanyId` claim
- `company_id` claim
- `HttpContext.Session["CompanyId"]`
- query parameter `company_id`, only honored for super admin

### Company-Wise Query Patterns

Common secure query pattern:

```text
tbl_session -> tbl_user -> company_id
```

This is used when session/log rows do not directly store company ID.

Example:

```text
tbl_network_log.session_id
    -> tbl_session.id
    -> tbl_session.user_id
    -> tbl_user.id
    -> tbl_user.company_id
```

For project data, the common pattern is:

```text
tbl_project.company_id
```

or fallback ownership:

```text
tbl_project.created_by_user_id
```

For polygons:

```text
map_regions.tbl_project_id -> tbl_project.id -> tbl_project.company_id
```

or for reusable/user-created polygons:

```text
map_regions.created_by_user_id
```

### Company-Wise Examples

| Scenario | What backend does |
|---|---|
| Super admin calls `GET /api/MapView/GetProjects` without `company_id` | Returns global project list where endpoint supports global scope. |
| Super admin calls `GET /api/MapView/GetProjects?company_id=5` | Returns company 5 projects. |
| Admin from company 5 calls `GET /api/MapView/GetProjects?company_id=9` | Ignores requested 9 and uses company 5. |
| Normal user has no valid company context | Endpoint may return unauthorized, or fallback to user-created rows if implemented. |
| Admin calls session/log analytics | Backend joins logs -> sessions -> users and filters by admin's company ID. |

## 6.3 License and Feature Access

Some features are controlled by license records.

License-related tables:

- `tbl_company_license_grant_history`
- `tbl_company_user_license_issued`
- `license_feature_access`

Feature codes:

- `report_generation`
- `benchmark_tab`
- `run_prediction`
- `grid_fetch`

`LicenseFeatureService` normalizes aliases. For example, `report`, `generate_report`, and `pdf_report` map to `report_generation`.

Feature behavior:

- Super admins get all default features.
- Normal/admin users need an active issued license.
- Active license condition includes `status = 1` and `valid_till >= UTC_DATE()`.
- `GridAnalyticsController` specifically checks `grid_fetch`.

If a feature API returns `403 FEATURE_NOT_ENABLED`, check:

1. User ID from auth claims/session.
2. Active row in `tbl_company_user_license_issued`.
3. Feature row in `license_feature_access`.
4. Feature code spelling/aliases.

## 6.4 Knowledge Transfer: How To Trace Any Request

To understand any backend request, follow this checklist:

1. Identify route/controller.
   - Example: `/api/MapView/GetProjects` -> `MapViewController.GetProjects`.

2. Identify authentication type.
   - `[Authorize]` means cookie login required.
   - `[AllowAnonymous] + [PublicApiKey]` means public API key required.
   - Python bridge means `X-Python-Bridge-Key` required.

3. Identify selected database.
   - Look at query/header/claim/session `country_code`.
   - `TW` means secondary DB unless path is pinned to main DB.
   - Otherwise main DB.

4. Identify company/user scope.
   - Super admin can use requested `company_id`.
   - Admin/user is forced to own company.
   - Missing company may fallback to current user or return unauthorized.

5. Identify tables touched.
   - Session/log flow: `tbl_session`, `tbl_network_log`, `tbl_network_log_neighbour`.
   - Project/polygon flow: `tbl_project`, `map_regions`.
   - Prediction flow: prediction tables.
   - Upload flow: `tbl_upload_history` plus parsed target tables.

6. Check Redis.
   - Read APIs may return cached data.
   - Write APIs often invalidate `mapview:*`, `networklog:*`, polygon, and date-range cache keys.

7. Check response shape.
   - Most APIs return `Status`, `Message`, and `Data`.
   - Some newer APIs return `success`, `message`, or direct objects.

## 6.5 Operational KT: Common Backend Tasks

### Add a New Company

Use:

- `POST /api/company/SaveCompanyDetails`

Backend work:

1. Checks caller is super admin for company creation.
2. Inserts company into `tbl_company`.
3. Creates default admin user for that company.
4. May mirror company/user into secondary DB depending on code path/config.
5. Returns company/default user details.

After creation:

- Grant company/user license if feature-gated modules are needed.
- Confirm `tbl_user.company_id` is set.

### Grant Feature Access

Use:

- `POST /api/company/grantLicense`
- optionally update license features through company license update paths.

Backend work:

1. Creates/updates license grant.
2. Creates/updates issued license rows.
3. Saves feature codes in `license_feature_access`.

Important:

- Without `grid_fetch`, grid analytics API will block normal users.
- Without `run_prediction`, frontend should not allow prediction flows if it checks enabled features.

### Upload Drive-Test CSV

Use:

- `POST /ExcelUpload/UploadExcelFile`

Backend work:

1. Saves uploaded file under `UploadedExcels`.
2. Inserts `tbl_upload_history` with processing status.
3. Calls `ProcessCSVController.Process`.
4. Inserts sessions/logs/prediction/project rows depending on upload type.
5. Updates upload status and errors.

### Mobile Drive-Test Collection

Use:

- `POST /api/MapView/user_signup`
- `POST /api/MapView/start_session`
- `POST /api/MapView/log_networkAsync`
- `POST /api/MapView/end_session`

Backend work:

1. Register or reuse mobile user/device.
2. Create session.
3. Insert network log samples and neighbor cells.
4. End session with distance/location summary.
5. Analytics APIs consume these logs.

### Create Project and Analyze

Use:

- `POST /api/MapView/createProject`
- `POST /api/MapView/SavePolygon`
- `POST /api/MapView/CreateProjectWithPolygons`
- `GET /api/MapView/GetNetworkLog`
- `GET /api/MapView/kpi-distribution`
- `GET /api/MapView/GetSubSessionAnalytics`

Backend work:

1. Creates project.
2. Stores project polygons as MySQL geometry.
3. Links sessions/logs/prediction records to project.
4. Returns map points and KPI summaries.

### Run Prediction / ML Integration

There are two sides:

Dashboard/API side:

- `POST /api/MapView/UploadSitePredictionCsv`
- `GET /api/MapView/GetSitePrediction`
- `GET /api/MapView/CompareSitePrediction`
- `GET /api/MapView/GetSitePredictionOptimised`

Python side:

- `GET /api/PythonBridge/GetLteBaselineRows`
- `POST /api/PythonBridge/SaveLtePredictionOptimisedResults`
- `POST /api/PythonBridge/CreateLteOptimizationScenario`
- `POST /api/PythonBridge/UpdateLteOptimizationScenarioStatus`

Backend work:

1. Baseline/input rows are stored.
2. Python fetches rows using bridge key.
3. Python saves optimized/refined/geo/RF results.
4. UI compares and visualizes baseline vs optimized.

### Compute Grid Analytics

Use:

- `POST /api/GridAnalytics/ComputeAndStoreGridAnalytics?projectId=...`
- `GET /api/GridAnalytics/GetGridAnalytics?projectId=...`

Backend work:

1. Checks license feature `grid_fetch`.
2. Resolves project and company scope.
3. Builds grid over map region or prediction bounds.
4. Maps baseline/optimized points to each grid cell.
5. Calculates avg/median/min/max/mode RSRP/RSRQ/SINR.
6. Stores rows in `grid_analytics_results`.
7. Read API returns latest grid generation.

## 7. Main Data Model Areas

`ApplicationDbContext` maps these major table groups:

Users and auth:

- `tbl_user`
- `tbl_user_deletion_otp`
- `tbl_user_deletion_token`
- `tbl_user_deletion_audit`
- `tbl_user_login_audit_details`
- `m_user_type`
- `m_email_setting`

Company and licensing:

- `tbl_company`
- `tbl_company_license_grant_history`
- `tbl_company_user_license_issued`

Sessions and network logs:

- `tbl_session`
- `tbl_network_log`
- `tbl_network_log_neighbour`

Prediction and thresholds:

- `tbl_prediction_data`
- `site_prediction`
- `site_prediction_optimized`
- `thresholds`
- `tbl_lte_prediction_results`
- `tbl_lte_prediction_results_refined`
- `lte_prediction_baseline_results`
- `lte_prediction_optimised_results`

Projects, polygons, uploads, and analytics:

- `tbl_project`
- `map_regions`
- `tbl_upload_history`
- `tbl_dashboard_cache`
- `grid_analytics_results`

Raw SQL/keyless DTO result models:

- N78 neighbor DTOs
- KPI distribution rows
- band/operator/network DTOs
- temporary location DTOs

## 8. Controller Map

## 8.1 Backend Product Concept

This backend supports a telecom/network tracking product. The main business concepts are:

- Users log in from the web dashboard or register from the mobile app.
- Companies own users, licenses, sessions, projects, polygons, and analytics data.
- A mobile drive-test app records location and radio/network KPI samples.
- Sessions group the collected network samples.
- Network logs store signal values such as RSRP, RSRQ, SINR, PCI, band, technology, throughput, latency, jitter, packet loss, indoor/outdoor, and neighbor cell data.
- Projects group selected sessions, polygons, and site-prediction data for analysis.
- Polygons/regions define map areas or buildings where logs/predictions are analyzed.
- Prediction APIs store and compare baseline, optimized, ML, and non-ML site prediction results.
- Grid analytics splits a project area into grid cells and compares baseline vs optimized network quality.
- Python bridge APIs allow external Python/ML/reporting scripts to fetch backend data and save computed results.
- Redis speeds up expensive analytics and prevents duplicate active logins.

In simple terms:

```text
User/Company -> Sessions -> Network Logs -> Projects/Polygons -> Analytics/Prediction -> Dashboard/Reports
```

## 8.2 API Authentication Types

There are three API access styles in this backend:

| API type | Used by | Auth required | Example |
|---|---|---|---|
| Web dashboard APIs | React/MVC/admin frontend | Cookie login through `/api/auth/login` | `/api/MapView/GetProjects` |
| Public mobile/helper APIs | Mobile app, health/download helpers | Public API key header | `/api/MapView/log_networkAsync` |
| Python bridge APIs | Python scripts/jobs | `X-Python-Bridge-Key` header | `/api/PythonBridge/GetDriveTestRows` |

Public API key headers:

- `X-Public-Api-Key`
- or `X-API-Key`

Python bridge header:

- `X-Python-Bridge-Key`

For authenticated dashboard requests, the browser must send the auth/session cookies:

- `st.auth`
- `st.session`

If CSRF is enabled, mutating authenticated requests also need:

- cookie: `st.csrf`
- header: `X-CSRF-TOKEN`

## 8.3 API Work Catalog

This section explains which API is used for which backend work.

### AuthController

Base route: `/api/auth`

Purpose:

- Login/logout.
- Current auth status.
- Feature list resolution.
- Redis login lock handling.
- Main/TW DB login routing.

Endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/auth/login` | Logs in dashboard/admin user. | Validates password, selects main/TW DB, creates Redis login lock, writes auth cookie/session, returns user/features. |
| POST | `/api/auth/logout` | Logs out current user. | Deletes Redis login lock, clears auth cookie and session. |
| GET | `/api/auth/status` | Checks if browser is logged in. | Reads claims/cookie, fetches user summary, returns enabled license features. |

### CompanyController

Base route: `/api/company`

Purpose:

- Company CRUD.
- Default admin creation.
- License grant/revoke/update.
- Company user management.

Important endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/api/company/GetAll` | Lists companies. | Applies super-admin/company scope and returns company/license summary. |
| POST | `/api/company/SaveCompanyDetails` | Creates/updates company. | Saves company details, can create default admin user, may mirror company/user into secondary DB. |
| DELETE | `/api/company/deleteCompany?id=` | Soft/deletes company. | Checks super-admin access and updates/deletes company record. |
| GET | `/api/company/grantLicenseHistory` | Gets license grant history. | Reads `tbl_company_license_grant_history` with company scope. |
| POST | `/api/company/grantLicense` | Grants license to company. | Inserts/updates license grant and history rows. |
| GET | `/api/company/usedLicenses` | Lists used licenses/users. | Reads issued license rows and associated users. |
| POST | `/api/company/revokeLicense?licenseId=` | Revokes an issued license. | Marks issued license inactive/revoked. |
| PUT | `/api/company/updateUser?userId=` | Updates company user. | Updates user info, role, active state, company-safe fields. |
| POST | `/api/company/revokeUser?userId=` | Revokes user access. | Marks user/license inactive. |
| DELETE | `/api/company/deleteUser?userId=` | Soft deletes company user. | Marks user deleted after scope checks. |
| PUT | `/api/company/updateIssuedLicense?licenseId=` | Updates issued license. | Changes issued license feature/status/details. |

### AdminController

Base route: `/Admin`

Purpose:

- Admin web pages.
- Dashboard data.
- User listing.
- Redis diagnostics.
- DB diagnostics.

Important endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/Admin/Index` | Admin landing view. | Returns MVC view if authenticated. |
| GET | `/Admin/Dashboard` | Admin dashboard view. | Returns MVC dashboard page. |
| GET | `/Admin/DbDiagnostics` | Shows DB/session diagnostics. | Returns current auth claims, session, selected DB info. |
| GET | `/Admin/GetUsers` | Lists users. | Applies company scope, optional filters, joins license data, caches where possible. |
| GET | `/Admin/GetUserById` | Gets one user. | Reads user by ID with scope validation. |
| POST | `/Admin/SaveUserDetails` | Creates/updates user. | Saves user profile, password, role/company fields. |
| POST | `/Admin/DeleteUser` | Soft deletes user. | Marks user deleted/inactive. |
| POST | `/Admin/ActivateUser` | Activates user. | Updates user active flag. |
| POST | `/Admin/InactivateUser` | Inactivates user. | Updates user active flag. |
| POST | `/Admin/DeleteUserPermanent` | Permanently deletes user. | Removes user after checks. |
| POST | `/Admin/UserResetPassword` | Admin resets password. | Hashes and saves new password. |
| POST | `/Admin/ChangePassword` | User changes password. | Verifies old password and saves new hash. |
| GET | `/Admin/GetAllNetworkLogs` | Returns network logs for dashboard. | Reads log data with filters and caching. |
| GET | `/Admin/GetAllNetworkLogsPaged` | Paged network logs. | Efficient paginated log read. |
| GET | `/Admin/GetOperatorCoverageRanking` | Operator coverage ranking. | Aggregates coverage KPIs by operator. |
| GET | `/Admin/GetOperatorQualityRanking` | Operator quality ranking. | Aggregates quality KPIs by operator. |
| GET | `/Admin/IndoorCount` | Indoor sample count. | Counts indoor logs. |
| GET | `/Admin/OutdoorCount` | Outdoor sample count. | Counts outdoor logs. |
| GET | `/Admin/IndoorKpis` | Indoor KPI summary. | Aggregates indoor KPI values. |
| GET | `/Admin/OutdoorKpis` | Outdoor KPI summary. | Aggregates outdoor KPI values. |
| GET | `/Admin/GetSessions` | Lists sessions. | Reads sessions with user/company scope. |
| GET | `/Admin/GetSessionsByDateRange` | Sessions by date range. | Filters sessions between dates. |
| DELETE | `/Admin/DeleteSession?id=` | Deletes a session. | Deletes session and cascaded network logs. |
| GET | `/Admin/TotalsV2` | Dashboard totals. | Aggregates user/session/log totals. |
| GET | `/Admin/MonthlySamplesV2` | Monthly sample chart. | Aggregates sample counts by month. |
| GET | `/Admin/OperatorSamplesV2` | Operator sample chart. | Aggregates samples by operator. |
| GET | `/Admin/NetworkTypeDistributionV2` | Network type chart. | Aggregates 2G/3G/4G/5G distribution. |
| GET | `/Admin/AvgRsrpV2`, `/AvgRsrqV2`, `/AvgSinrV2` | Average radio KPI charts. | Aggregates selected KPI by operator/network/date. |
| GET | `/Admin/redis/keys` | Lists Redis keys. | Reads Redis keys by pattern. |
| GET | `/Admin/redis/key-info` | Gets Redis key details. | Reads TTL/type/value preview. |
| POST | `/Admin/redis/extend-ttl` | Extends Redis TTL. | Updates key expiration. |
| DELETE | `/Admin/redis/delete` | Deletes Redis key. | Removes one key. |
| POST | `/Admin/redis/flush` | Flushes Redis. | Clears Redis cache. |

### MapViewController

Base route: `/api/MapView`

Purpose:

- Mobile user signup/session lifecycle.
- Network log ingestion.
- Map polygons.
- Projects.
- Network analytics.
- KPI distributions.
- Prediction data.
- Site prediction baseline/optimized data.
- Redis-backed map analytics caching.

Main endpoint groups:

Mobile/public ingestion:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/MapView/user_signup` | Registers mobile user/device. | Checks duplicate device/mobile, inserts `tbl_user`, returns `userid`. |
| POST | `/api/MapView/start_session` | Starts a drive-test session. | Inserts `tbl_session`, invalidates map caches, returns `sessionid`. |
| POST | `/api/MapView/end_session` | Ends a drive-test session. | Updates session end time, distance, start/end location/address, invalidates caches. |
| POST | `/api/MapView/log_networkAsync` | Saves mobile network samples. | Inserts `tbl_network_log` and neighbor records, parses radio data, invalidates caches. |
| POST | `/api/MapView/UploadImage` | Uploads image from mobile/client. | Saves uploaded file and returns path/name. |
| POST | `/api/MapView/UploadImageLegacy` | Legacy image upload. | Same purpose for older clients. |

Polygons and projects:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/api/MapView/GetProjects` | Lists projects user can access. | Applies company/user scope, uses Redis cache, returns project metadata. |
| POST | `/api/MapView/createProject` | Creates simple project. | Inserts `tbl_project`. |
| POST | `/api/MapView/CreateProjectWithPolygons` | Creates project and polygons. | Inserts project and `map_regions`, links selected sessions/logs. |
| DELETE | `/api/MapView/DeleteProject?projectId=` | Deletes project. | Scope-checks project, deletes/marks project and invalidates caches. |
| GET | `/api/MapView/GetProjectPolygons` | Gets polygons for project. | Reads `map_regions` and returns geometry/log counts. |
| GET | `/api/MapView/GetProjectPolygonsV2` | Gets project polygons, V2. | Reads project region polygons and/or site-prediction source. |
| POST | `/api/MapView/SavePolygon` | Saves a polygon. | Converts coordinates to MySQL geometry, inserts `map_regions`. |
| POST | `/api/MapView/SavePolygonWithLogs` | Saves polygon and attaches logs. | Stores polygon and selected/contained session log references. |
| POST | `/api/MapView/ImportPolygon` | Imports polygon. | Reads uploaded/imported polygon geometry and stores it. |
| GET | `/api/MapView/GetAvailablePolygons` | Lists saved polygons. | Applies company/user scope and returns active polygons. |
| DELETE | `/api/MapView/DeleteAvailablePolygon` | Deletes saved polygon. | Scope-checks and marks/removes polygon. |
| GET | `/api/MapView/GetPolygonLogCount` | Counts logs inside polygon. | Spatial/count query over network logs. |
| POST | `/api/MapView/AssignPolygonToProject` | Assigns polygon to project. | Updates `map_regions.tbl_project_id`. |
| GET | `/api/MapView/ListSavedPolygons` | Lists saved polygons for assignment. | Reads reusable polygons. |
| POST | `/api/MapView/AssignExistingSitePredictionToProject` | Links saved prediction/polygon to project. | Updates project-region/prediction association. |

Network log and map analytics:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/api/MapView/GetNetworkLog` | Returns map network logs. | Filters by date/session/operator/network/band/project/polygon, returns log points. |
| GET | `/api/MapView/GetLogsByDateRange` | Returns logs by date range. | Company/user-scoped log query with cache. |
| GET | `/api/MapView/GetNeighbourLogsByDateRange` | Returns neighbor logs by date range. | Queries `tbl_network_log_neighbour` joined to sessions/users. |
| GET | `/api/MapView/GetSubSessionAnalytics` | Session/sub-session analytics. | Groups sessions/logs and returns KPI summaries. |
| GET | `/api/MapView/GetSubSessionAnalyticsWithStatus` | Same as above with status. | Alias/rewrite adds `includeStatus=1`. |
| GET | `/api/MapView/session/provider-network-time/combined` | Provider/network usage time. | Aggregates time spent by provider and network type. |
| GET | `/api/MapView/kpi-distribution` | KPI distribution buckets. | Buckets values such as RSRP/RSRQ/SINR/throughput. |
| GET | `/api/MapView/lat-lon-distribution` | Location distribution. | Aggregates logs by lat/lon areas. |
| GET | `/api/MapView/GetN78NeighboursSimple` | Simple N78 neighbor summary. | Reads neighbor records for n78 detection. |
| GET | `/api/MapView/GetN78Neighbours` | N78 neighbor details. | Returns primary/neighbor 5G n78 details by session/project. |
| GET | `/api/MapView/GetProviderWiseVolume` | Provider-wise data volume. | Aggregates RX/TX/download/upload volume by provider. |
| GET | `/api/MapView/sessionsDistance` | Session distance totals. | Sums session distance for filters. |
| GET | `/api/MapView/GetTotalUsageTime` | Total usage time. | Aggregates session/log duration. |
| GET | `/api/MapView/GetIndoorOutdoorSessionAnalytics` | Indoor/outdoor analytics. | Splits session/log KPIs by indoor/outdoor flag. |
| GET | `/api/MapView/GetProviders` | Provider dropdown data. | Distinct operators/providers from logs. |
| GET | `/api/MapView/GetTechnologies` | Technology dropdown data. | Distinct network/technology values. |
| GET | `/api/MapView/GetBands` | Band dropdown data. | Distinct band values. |
| GET | `/api/MapView/GetDominanceDetails` | Dominance details. | Finds dominant/weak PCI/cell/operator conditions. |
| GET | `/api/MapView/GetPciDistribution` | PCI distribution. | Aggregates samples by PCI. |

Prediction/site prediction:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET/POST | `/api/MapView/GetPredictionLog` | Returns prediction log points. | Applies threshold colors/classes and returns prediction data for maps. |
| GET | `/api/MapView/GetPredictionDataForSelectedBuildingPolygonsRaw` | Raw prediction points for polygons. | Reads prediction rows inside selected building polygons. |
| POST | `/api/MapView/UploadSitePredictionCsv` | Uploads site prediction CSV. | Parses CSV and inserts baseline/site prediction records. |
| POST | `/api/MapView/EnsureSitePredictionOptimizedScenarioColumn` | DB compatibility helper. | Ensures optimized scenario column exists. |
| GET | `/api/MapView/GetUpdatedSitePrediction` | Gets updated/optimized prediction. | Returns optimized prediction rows for project/operator/scenario. |
| GET | `/api/MapView/GetSitePrediction` | Gets site prediction result. | Reads prediction table with filters/paging. |
| GET | `/api/MapView/CompareSitePrediction` | Compares baseline vs optimized. | Matches baseline and optimized rows and returns differences. |
| POST | `/api/MapView/UpdateSitePrediction` | Updates site prediction rows. | Reads request body, updates prediction values/scenario data. |
| GET | `/api/MapView/GetSitePredictionScenarios` | Lists scenarios for project. | Reads distinct optimized scenario IDs/status. |
| POST | `/api/MapView/DeleteSitePredictionScenario` | Deletes site prediction scenario. | Removes scenario rows. |
| POST | `/api/MapView/DeleteLtePredictionOptimisedScenario` | Deletes LTE optimized scenario. | Removes optimized scenario rows. |
| POST | `/api/MapView/DeleteSitePrediction` | Deletes site prediction data. | Removes prediction rows by requested filters. |
| GET | `/api/MapView/GetSiteNoMl` | Gets non-ML site prediction. | Reads non-ML prediction table/data. |
| GET | `/api/MapView/GetSiteMl` | Gets ML site prediction. | Reads ML prediction table/data. |
| POST | `/api/MapView/AddSitePrediction` | Adds site prediction row. | Inserts prediction row manually/API-driven. |
| GET | `/api/MapView/GetNeighboursForPrimary` | Neighbor details for primary cell. | Looks up neighbors by selected primary cell/site/session. |
| GET | `/api/MapView/GetLtePredictionStats` | LTE prediction metric stats. | Aggregates selected metric by project. |
| GET | `/api/MapView/GetLtePredictionLocationStats` | Location stats for LTE prediction. | Aggregates metric at a location or cell. |
| GET | `/api/MapView/GetLtePredictionLocationStatsRefined` | Refined prediction location stats. | Same idea using refined prediction table. |
| GET | `/api/MapView/GetSitePredictionBase` | Baseline prediction lookup. | Reads `lte_prediction_baseline_results` by project/node/cell/sector. |
| GET | `/api/MapView/GetSitePredictionOptimised` | Optimized prediction lookup. | Reads optimized rows first, falls back to baseline if optimized missing. |

Caching:

- Cache keys start with prefixes like `mapview:*`, `networklog:v2:*`, `networklog:v3:*`, `projectpolygons:*`, `availablepolygons:*`.
- Mutating endpoints invalidate related Redis patterns.

### ExcelUploadController

Base route: `/ExcelUpload`

Purpose:

- Upload CSV/ZIP/template files.
- Track upload history.
- Create project on project upload.
- Run CSV processing.
- Download templates and uploaded files.

Endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/ExcelUpload/Index` | Upload page. | Returns MVC upload view. |
| GET | `/ExcelUpload/DownloadExcel` | Downloads template/uploaded file. | Reads `Template-Files` or `UploadedExcels`; requires public API key for anonymous access. |
| GET | `/ExcelUpload/DownloadPythonRuntime` | Downloads Python runtime/template. | Calls `DownloadExcel(4, null)`. |
| GET | `/ExcelUpload/GetUploadedExcelFiles` | Lists recent uploads. | Reads `tbl_upload_history`, joins user/session, applies company scope. |
| POST | `/ExcelUpload/UploadExcelFile` | Uploads and processes CSV/ZIP. | Saves file, creates upload history, creates project if needed, calls CSV processor, updates status. |
| GET | `/ExcelUpload/GetSessions` | Lists sessions by date range. | Reads sessions for selected dates/company. |
| GET | `/ExcelUpload/Test` | Debug endpoint. | Returns basic controller/environment status. |

Upload flow:

1. User uploads a file and optional polygon/note file.
2. Files are saved under `UploadedExcels`.
3. A `tbl_upload_history` row is created with status `Processing`.
4. If upload type is project upload, a `tbl_project` row is created.
5. `ProcessCSVController.Process(...)` parses and inserts data.
6. Upload history is updated to success or failed.
7. For session uploads, the created session ID is returned.

### ProcessCSVController

Base route: `/api/ProcessCSV`

Purpose:

- Parse uploaded CSV/ZIP data.
- Insert network logs, neighbor logs, prediction rows, and project data.
- Provide upload-specific APIs for site prediction.

Important endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/ProcessCSV/upload/site-prediction` | Uploads site prediction CSV. | Parses file, inserts site prediction records. |
| GET | `/api/ProcessCSV/site-prediction` | Lists site prediction rows. | Reads prediction rows by project with paging. |

Also contains internal processing methods used by `ExcelUploadController`.

### PythonBridgeController

Base route: `/api/PythonBridge`

Purpose:

- Secure machine-to-machine API for Python scripts/jobs.
- Reads drive-test/session/project data.
- Saves prediction, baseline, optimized, refined, geo feature, and RF optimization results.

Security:

- Requires header `X-Python-Bridge-Key`.
- Key is validated by `PythonBridgeService`.

Important read endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/PythonBridge/GetDriveTestRows` | Gives Python drive-test samples. | Reads network logs for requested session IDs with paging. |
| GET | `/api/PythonBridge/GetLteTiltBaselineResults` | Gives tilt baseline rows. | Reads LTE tilt baseline data for project. |
| GET | `/api/PythonBridge/GetLteTiltAntennaRows` | Gives antenna rows. | Reads antenna/site rows for project. |
| GET | `/api/PythonBridge/GetLtePredictionGeoFeatures` | Gives geo features. | Reads prediction geo feature records. |
| GET | `/api/PythonBridge/GetSitePredictionOptimized` | Gives optimized site prediction. | Reads optimized site prediction rows by project/operator. |
| GET | `/api/PythonBridge/GetLteSitePredictionRows` | Gives LTE site prediction rows. | Reads LTE prediction data for Python processing. |
| GET | `/api/PythonBridge/GetLteBuildingRows` | Gives building rows. | Reads building/region data for project. |
| GET | `/api/PythonBridge/GetLteBaselineRows` | Gives baseline rows. | Reads LTE baseline prediction rows. |
| GET | `/api/PythonBridge/GetProject` | Gives one project. | Reads project metadata. |
| GET | `/api/PythonBridge/GetProjectRegions` | Gives project polygons. | Reads `map_regions` for project. |
| POST | `/api/PythonBridge/GetReportNetworkLogs` | Gives report logs. | Reads logs for report generation. |
| POST | `/api/PythonBridge/GetSessions` | Gives selected sessions. | Reads `tbl_session` by IDs. |
| GET | `/api/PythonBridge/GetUser` | Gives user info. | Reads `tbl_user` by ID. |
| GET | `/api/PythonBridge/GetUserThresholds` | Gives thresholds. | Reads user/default threshold settings. |

Important write endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/PythonBridge/SavePredictionData` | Saves generic prediction data. | Bulk inserts `tbl_prediction_data`. |
| POST | `/api/PythonBridge/SaveLtePredictionResults` | Saves LTE prediction results. | Bulk inserts LTE prediction result rows. |
| POST | `/api/PythonBridge/SaveLtePredictionRefined` | Saves refined LTE prediction. | Bulk inserts refined result rows. |
| POST | `/api/PythonBridge/SaveLtePredictionOptimisedResults` | Saves optimized LTE results. | Bulk inserts optimized rows/scenario results. |
| POST | `/api/PythonBridge/SaveLtePredictionBaselineResults` | Saves baseline LTE rows. | Bulk inserts baseline rows. |
| POST | `/api/PythonBridge/SaveLtePredictionGeoFeatures` | Saves geo features. | Bulk inserts feature rows. |
| POST | `/api/PythonBridge/DeleteLtePredictionGeoFeatures` | Deletes geo features. | Deletes matching feature rows. |
| POST | `/api/PythonBridge/CreateLteOptimizationScenario` | Creates optimization scenario. | Inserts scenario metadata and returns row/scenario ID. |
| POST | `/api/PythonBridge/UpdateLteOptimizationScenarioStatus` | Updates scenario status. | Updates scenario state/progress. |
| POST | `/api/PythonBridge/SaveRfOptimizationResults` | Saves RF optimization output. | Bulk inserts RF optimization result rows. |
| POST | `/api/PythonBridge/UpdateProjectDownloadPath` | Stores report/download path. | Updates project download path. |

Scenario helpers:

| Method | API | What it does |
|---|---|---|
| GET | `/api/PythonBridge/GetNextRfOptimizationScenarioId` | Returns next RF scenario ID for project. |
| GET | `/api/PythonBridge/GetLatestLteBaselineJobId` | Returns latest baseline job ID. |
| GET | `/api/PythonBridge/GetNextLteOptimizationScenarioId` | Returns next LTE optimization scenario ID. |
| GET | `/api/PythonBridge/PredictionDebugSummary` | Returns prediction/project debug counts. |

### DataDeletionController

Base route: `/api/data-deletion`

Purpose:

- User data deletion workflow.
- OTP send/verify.
- Data preview before deletion.
- Deletion request creation.

Endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/data-deletion/send-otp` | Sends deletion OTP. | Finds user by phone and sends OTP through OTP/SMS service. |
| POST | `/api/data-deletion/verify-otp` | Verifies OTP. | Validates OTP and returns deletion token. |
| GET | `/api/data-deletion/data-preview` | Shows data to be deleted. | Uses bearer/header deletion token to count/list user data. |
| POST | `/api/data-deletion/request-deletion` | Requests permanent deletion. | Validates token/confirmation and schedules deletion. |

### SettingController

Base route: `/api/Setting`

Purpose:

- User/company/default threshold settings.

Endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/api/Setting/GetThresholdSettings` | Gets KPI threshold config. | Returns user-specific thresholds, else default thresholds, else fallback row. |
| POST | `/api/Setting/SaveThreshold` | Saves KPI thresholds. | Updates latest user threshold or inserts a new user-specific threshold row. |

### GridAnalyticsController

Purpose:

- Grid-level analytics.
- Uses `grid_analytics_results`.

Base route: `/api/GridAnalytics`

| Method | API | What it does | Main backend work |
|---|---|---|---|
| POST | `/api/GridAnalytics/ComputeAndStoreGridAnalytics` | Computes project grid analytics. | Checks `grid_fetch` license, builds grid over polygon/project area, compares baseline vs optimized KPI values, stores results in `grid_analytics_results`. |
| POST | `/api/GridAnalytics/SetProjectGridSize` | Saves project grid size. | Updates `tbl_project.grid_size`. |
| GET | `/api/GridAnalytics/GetGridAnalytics` | Reads stored grid analytics. | Returns latest stored grid cells and KPI differences. |
| GET | `/api/GridAnalytics/GetCoverageOptimizationSummary` | Summarizes optimized coverage changes. | Compares baseline vs optimized rows and lists changed fields/sectors. |
| GET | `/api/GridAnalytics/GetOptimizationScenarios` | Lists optimization scenarios. | Reads scenario IDs/status for project. |

### HealthController

Base route: `/healthz`

Purpose:

- Liveness and readiness checks.

Endpoints:

| Method | API | What it does | Main backend work |
|---|---|---|---|
| GET | `/healthz` | Readiness check. | Checks main DB, TW DB, Redis status, uptime. |
| GET | `/healthz/ready` | Readiness check. | Same as `/healthz`. |
| GET | `/healthz/live` | Liveness check. | Returns service alive and uptime without DB dependency. |

Readiness checks:

- Main database.
- TW database.
- Redis status.
- Uptime.

### HomeController

Purpose:

- MVC web pages.
- Login page and authenticated page helpers.
- Current logged user helpers.

Base route: `/Home`

Important endpoints:

| Method | API | What it does |
|---|---|---|
| GET | `/Home` or `/Home/Index` | Login/home page. |
| GET | `/Home/Login` | Login page. |
| POST | `/Home/UserLogin` | Legacy MVC login flow. |
| POST | `/Home/GetUserForgotPassword` | Starts forgot password flow. |
| GET | `/Home/ResetPassword` | Reset password page. |
| POST | `/Home/ForgotResetPassword` | Saves reset password. |
| GET | `/Home/Logout` | Legacy logout. |
| POST | `/Home/GetLoggedUser` | Returns logged-in user/session info. |

### RedisTestController

Purpose:

- Redis test/diagnostic APIs.

Base route: `/api/RedisTest`

| Method | API | What it does |
|---|---|---|
| GET | `/api/RedisTest/test` | Pings Redis, writes a test key, reads it back, deletes it, and returns Redis health. |

## 9. Common Backend Flows

### A. Web User Login and Dashboard Flow

1. Frontend calls `POST /api/auth/login`.
2. Backend validates credentials from main or TW DB.
3. Backend creates auth cookie and session.
4. Frontend calls `GET /api/auth/status`.
5. User opens dashboard/admin/map pages.
6. Protected APIs use claims/session to resolve:
   - user ID
   - role
   - company ID
   - country/database
7. Queries run against the selected MySQL database.
8. Responses are returned as JSON or MVC views.

### B. Mobile App Drive Test Flow

1. Mobile app registers user with `POST /api/MapView/user_signup`.
2. Mobile app starts a drive session with `POST /api/MapView/start_session`.
3. Mobile app sends network samples to `POST /api/MapView/log_networkAsync`.
4. Optional images are uploaded through `UploadImage` or `UploadImageLegacy`.
5. Mobile app ends the session with `POST /api/MapView/end_session`.
6. Backend stores data in:
   - `tbl_user`
   - `tbl_session`
   - `tbl_network_log`
   - `tbl_network_log_neighbour`
7. Analytics endpoints read this data for maps, KPI charts, provider distribution, and session summaries.

### C. CSV Upload Flow

1. User opens upload UI.
2. User uploads CSV/ZIP using `POST /ExcelUpload/UploadExcelFile`.
3. File is stored in `UploadedExcels`.
4. Upload history is stored in `tbl_upload_history`.
5. If project upload, `tbl_project` is created.
6. `ProcessCSVController.Process(...)` parses the file.
7. Parsed data is inserted into session/log/prediction/project tables.
8. Upload history is marked success or failed.
9. Cache is invalidated where needed.

### D. Map/Analytics Flow

1. Frontend calls authenticated `/api/MapView/*` endpoint.
2. `UserScopeService` determines company or user scope.
3. `DbConnectionProvider` determines main/TW database.
4. Controller checks Redis cache for heavy queries.
5. If cache hit, cached response is returned.
6. If cache miss, EF Core or raw SQL queries MySQL.
7. Response is written to Redis with TTL.
8. Mutating operations invalidate matching Redis key patterns.

### E. Site Prediction Flow

1. Prediction input is uploaded through CSV/API endpoints.
2. Data is stored in prediction-related tables:
   - `tbl_prediction_data`
   - `site_prediction`
   - `site_prediction_optimized`
   - `tbl_lte_prediction_results`
   - `tbl_lte_prediction_results_refined`
   - `lte_prediction_baseline_results`
   - `lte_prediction_optimised_results`
3. Python jobs can fetch input rows through `PythonBridgeController`.
4. Python jobs save calculated results back through bridge save endpoints.
5. UI reads prediction output through `MapViewController` endpoints.
6. Optimized and baseline results can be compared through `CompareSitePrediction`.

### F. Python/ML Bridge Flow

1. Python job sends request to `/api/PythonBridge/*`.
2. Request includes `X-Python-Bridge-Key`.
3. Controller validates the key.
4. Controller validates required request fields.
5. `PythonBridgeService` executes database read/write logic.
6. Response returns count, paging info, inserted count, or scenario IDs.

### G. Data Deletion Flow

1. User requests OTP through `/api/data-deletion/send-otp`.
2. User verifies OTP through `/api/data-deletion/verify-otp`.
3. User can preview deletable data through `/api/data-deletion/data-preview`.
4. User submits deletion request through `/api/data-deletion/request-deletion`.
5. Optional hosted cleanup service processes pending deletion requests if enabled.

## 10. Security Notes

- Protected APIs rely on cookie authentication and `[Authorize]`.
- Public ingestion/download APIs require public API key.
- Python bridge APIs require `X-Python-Bridge-Key`.
- CSRF protection can be enabled with `Security:RequireCsrfHeader`.
- CSRF header name is `X-CSRF-TOKEN`.
- CSRF cookie name is `st.csrf`.
- Auth cookie name is `st.auth`.
- Session cookie name is `st.session`.
- Uploads are limited and path traversal is guarded by `Path.GetFileName` in download paths.
- Production error responses are filtered by `ProductionErrorResponseFilter`.
- Exceptions are sanitized through `SafeException` in many controllers.

## 11. Caching Notes

Redis is used for:

- Login lock.
- MapView response caching.
- Project/polygon/network analytics caching.
- Admin Redis diagnostics.

Important cache invalidation patterns:

- `mapview:*`
- `projectpolygons:*`
- `availablepolygons:*`
- `networklog:v2:*`
- `networklog:v3:*`
- `latlon:dist:*`
- `n78_simple_kpi:*`
- `n78_neighbours:*`
- `daterangelog:*`

## 12. Configuration Keys

Important configuration values:

- `ConnectionStrings:MySqlConnection`
- `ConnectionStrings:MySqlConnection2`
- `ConnectionStrings:Redis`
- `Security:PublicApiKey`
- `Security:RequireCsrfHeader`
- `Security:RequireRedisLoginLock`
- `Security:SessionIdleMinutes`
- `Security:MaxUploadBytes`
- `Security:AllowedOrigins`
- `Security:AllowNullOrigin`
- `Security:AllowLoopbackOrigins`
- `Security:ForwardedHeaders:KnownProxies`
- `Security:ForwardedHeaders:KnownNetworks`
- `UserDeletionCleanup:Enabled`

## 13. High-Level Backend Diagram

```text
Client / Mobile / Python Job
        |
        v
ASP.NET Core Middleware
Security headers -> HTTPS -> CORS -> Session -> Auth -> CSRF -> Authorization
        |
        v
Controllers
Auth | Admin | Company | MapView | ExcelUpload | ProcessCSV | PythonBridge | DataDeletion | Settings | Health
        |
        v
Services
UserScope | LicenseFeature | DbConnectionProvider | RedisService | PythonBridge | OTP/SMS | UserDeletion
        |
        v
Data Stores
MySQL Main DB / MySQL TW DB / Redis / UploadedExcels / Template-Files
```

## 14. File-Level KT Map

Use this map when taking ownership of the backend.

| Area | Main files | Main tables/services |
|---|---|---|
| App startup | `Program.cs` | DI, middleware, Redis, DB, upload limits |
| Security config | `Configuration/SecurityServiceExtensions.cs` | CORS, cookie auth, session, data protection |
| Dynamic DB routing | `Services/DbConnectionProvider.cs` | `MySqlConnection`, `MySqlConnection2`, country code |
| Company/user scope | `Services/UserScopeService.cs` | claims/session `CompanyId`, `UserTypeId` |
| Auth/login | `Controllers/AuthController.cs`, `Security/PasswordSecurity.cs` | `tbl_user`, Redis login lock |
| Admin dashboard | `Controllers/AdminController.cs` | users, sessions, logs, Redis diagnostics |
| Company/license | `Controllers/CompanyController.cs`, `Services/LicenseFeatureService.cs` | company, license, feature tables |
| Mobile/map/log APIs | `Controllers/MapViewController.cs` | sessions, logs, neighbors, projects, polygons |
| CSV upload | `Controllers/ExcelUploadController.cs`, `Controllers/ProcessCSVController.cs` | `tbl_upload_history`, parsed target tables |
| Python/ML bridge | `Controllers/PythonBridgeController.cs`, `Services/PythonBridgeService.cs` | prediction/log/project tables |
| Grid analytics | `Controllers/GridAnalyticsController.cs` | `grid_analytics_results`, prediction tables |
| Data deletion | `Controllers/DataDeletionController.cs`, `Services/OtpService.cs`, `Services/UserDeletionService.cs`, `Services/UserDeletionCleanupService.cs` | deletion OTP/token/audit tables |
| Settings | `Controllers/SettingController.cs` | `thresholds` |
| Health/Redis checks | `Controllers/HealthController.cs`, `Controllers/RedisTestController.cs` | DB connectivity, Redis connectivity |
| EF model mapping | `Models/AppDbContext.cs`, `Models/EntityModel.cs` | all DbSet/table mappings |

## 15. Main Table Relationship KT

Core user/company:

```text
tbl_company.id
    -> tbl_user.company_id
    -> tbl_session.user_id
    -> tbl_network_log.session_id
    -> tbl_network_log_neighbour.session_id
```

Project/polygon:

```text
tbl_company.id
    -> tbl_project.company_id
    -> map_regions.tbl_project_id
```

Upload/session:

```text
tbl_upload_history.id
    -> tbl_session.tbl_upload_id
```

Project/prediction:

```text
tbl_project.id
    -> tbl_prediction_data.tbl_project_id
    -> site_prediction.tbl_project_id
    -> site_prediction_optimized.tbl_project_id
    -> lte_prediction_baseline_results.project_id
    -> lte_prediction_optimised_results.project_id
    -> tbl_lte_prediction_results.project_id
    -> tbl_lte_prediction_results_refined.project_id
```

License/features:

```text
tbl_company.id
    -> tbl_company_user_license_issued.tbl_company_id
    -> tbl_company_user_license_issued.tbl_user_id
    -> license_feature_access.license_id
```

Deletion:

```text
tbl_user.id
    -> tbl_user_deletion_otp.user_id
    -> tbl_user_deletion_token.user_id
    -> tbl_user_deletion_audit.user_id
```

## 16. Things To Be Careful About

- `MapViewController.cs` is very large and contains many unrelated API groups. When changing it, search for the exact route and nearby helper methods before editing.
- Do not assume one database. Always confirm country context: main DB vs TW DB.
- Do not trust requested `company_id` for normal users. `UserScopeService` intentionally overrides it with the logged-in company.
- Some older data may not have direct `company_id`, so code often scopes through `tbl_user` or `created_by_user_id`.
- Redis can make analytics look stale if cache invalidation is missed. Mutating log/project/polygon/prediction APIs should invalidate related cache patterns.
- Some endpoints use EF Core; others use raw SQL because they need spatial queries, dynamic columns, or performance.
- Public mobile APIs are anonymous but protected by public API key. In production, `Security:PublicApiKey` must be configured.
- Python bridge APIs do not use normal user cookie auth. They use `X-Python-Bridge-Key`.
- Upload size differs by environment: production default is 100 MB unless configured; development config allows 500 MB.
- Date/time values are mixed between UTC and local/IST in places. Be careful when adding date filters.
- Many APIs return `Status = 1/0`; newer deletion APIs return `success = true/false`.

## 17. Fast KT Summary

If you only remember one mental model, use this:

```text
Country chooses database.
Company chooses data scope inside that database.
Role decides whether requested company_id is honored.
Session/log tables hold collected drive-test data.
Project/polygon tables organize data for map analysis.
Prediction tables hold baseline/optimized/ML outputs.
Python bridge moves data between backend and ML/report scripts.
Redis caches expensive analytics and controls active login locks.
```
