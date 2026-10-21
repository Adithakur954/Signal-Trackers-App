# GetNetworkLog performance

## Compatibility

GET and POST still use the same endpoint core. Response fields, row ordering,
default limit (20,000), maximum limit (50,000), page/cursor semantics, formulas,
provider fallback queries, project fallback, total counts and summary inclusion
rules are unchanged. Cache-state metadata still uses HIT/MISS/BYPASS.

## Changes

- One cache publication per page, instead of duplicating the response under an
  unread `latest` key. The per-key gate covers lookup, build and publication.
- Bounded local fallback holds up to 100,000 rows across pages when Redis is
  unavailable. A real Redis miss removes the local entry, respecting invalidation.
- Aggregate results have a separate versioned cache (256 local entries), shared
  across page sizes/cursors. Original SQL and calculation code are unchanged.
- Both caches expire absolutely after five minutes. Local hits do not extend
  Redis TTL. Mutation invalidation also works while Redis is disconnected.
- Keys include the current database version; page keys also hash the connection
  string and retain full date-filter precision. No credentials are emitted.
- Extra JSON fallback mapping parses the root once instead of twice.
- Request-time column/index creation and obsolete Redis pattern scans are removed.

## Deployment

Review and run `Database/get_network_log_maintenance.sql` separately for each
region database during a maintenance window. This is not executed automatically.
It checks columns and equivalent indexes before creating missing ones. In
particular `updated_at` is needed to detect edits to existing network logs.
Existing v29/v30 cache entries expire naturally; v31 keys exclude pages and
summaries that may have been built from dirty or inconsistent reads.

## Read consistency

Each request opens one REPEATABLE READ transaction. The version query, page rows,
app summary and combined statistics use that same connection and transaction.
SQL commands run sequentially on it, not concurrently across separate connections.
This prevents a failed import's uncommitted rows from reaching a page while its
total count excludes them. SQL predicates, ordering and aggregate formulas remain
unchanged. `X-Read-Isolation: REPEATABLE-READ` identifies the new request path.

This is a per-request snapshot, not a snapshot spanning HTTP page requests. If an
import commits between page 1 and page 2, their `data_version` values can differ.
The caller must not combine different versions into one complete dataset. Stop
the import and retry for a stable load; fully supporting live multi-page loads
requires a client-carried snapshot/version token (or a materialized dataset).
The frontend completeness guard must not be disabled or the count fabricated.
The existing version fingerprint's same-second/update limitations also remain.

## Remaining costs and measurement

Session validation and the filtered COUNT/MAX version query intentionally remain
on every request, including cache hits, to preserve immediate insert visibility
and the existing `data_version` calculation. Skipping them with a short TTL would
introduce stale results. Eliminating that scan requires a separately designed,
transactionally maintained version/summary table covering every write path.

Provider correlated lookups, text/regex predicates, polygon tests, OFFSET, default
row limits and the first summary scan remain unchanged. Changing these without
query-plan/data equivalence measurements would risk result or calculation changes.

Compare identical authenticated requests with the same sessions and filters:

- `X-Session-Validation-Ms`: valid-session existence check.
- `X-Data-Version-Ms`: authoritative filtered version query.
- `X-Page-Load-Ms`: cache read, or build and publication on a miss.
- `X-Cache`, `X-Cache-Store`: HIT/MISS/BYPASS and REDIS/MEMORY/DATABASE.
- `X-Total-Ms`: endpoint time; middleware `Server-Timing` also includes authentication.

Do not describe controller-only timing as full browser response time. JSON output,
transfer, authentication and rendering also contribute. No latency target has
been verified against a live database for this change.

## Regression

`dotnet run --project CallAnalyzerRegression -- --network-log-cache`

Covers cache coalescing/publication, one Redis write, scope/version separation,
absolute expiry, force refresh, offline and in-flight invalidation, Redis outage
and timeout fallback, Redis deletion, response JSON round-trip and JSON fallback
precedence/rounding. Existing sessions-pagination and session-continuity suites
should also pass. These tests do not run DDL or contact a live database.
