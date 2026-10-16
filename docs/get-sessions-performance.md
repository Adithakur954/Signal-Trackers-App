# GetSessions Performance

`GET /Admin/GetSessions?page=2&pageSize=100` keeps the existing exact totals.
A cold request must find every matching session to calculate those totals.
Matching IDs are cached for five minutes and shared across page sizes/pages;
the details query only reads the selected IDs and rechecks their network data.

For a bounded page query without an exact total, use:

```text
/Admin/GetSessions?page=2&pageSize=100&includeTotalCount=false
```

This mode reads at most `pageSize + 1` matching IDs after the page offset.
It returns `TotalCount: null`, `TotalPages: null`, `IsTotalCountExact: false`
and `HasMore`. It does not clamp out-of-range pages to the last page.
Exact-count mode remains the default; existing callers retain numeric totals.

Redis is authoritative when connected. When it is disconnected, the same app
instance uses an in-memory cache instead. `Source` is `DATABASE`, `REDIS`, or
`MEMORY`. Memory caching does not remove the first cold database scan.
Admin cache invalidation clears the local generation even when Redis is offline.
Local entries retain the original absolute expiry and never extend freshness.

Inspect `Server-Timing`, `X-Cache`, `X-Cache-Store`,
`X-Session-Index-Cache`, and `X-Session-Index-Store` to distinguish
query time from cache misses. These timings cover controller execution,
not authentication middleware, response transfer, or browser rendering.

Apply `Database/get_sessions_indexes.sql` separately during database maintenance
where its indexes are missing. The endpoint never creates indexes at runtime.

Regression checks:

```text
dotnet build CallAnalyzerRegression/CallAnalyzerRegression.csproj -o artifacts/sessions-pagination-regression /p:UseAppHost=false
dotnet artifacts/sessions-pagination-regression/CallAnalyzerRegression.dll --sessions-pagination
```
