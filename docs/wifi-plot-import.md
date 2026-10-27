# WiFi_plot.csv import and GetNetworkLog

Implemented 27 October 2026. This adds companion-file ingestion; existing network, L3 and Event formats retain their existing import logic.

## Covered entry paths

- Manual network ZIP/CSV imports through `ExcelUploadController` / `ProcessCSVController`. ZIP companion filenames are recognized case-insensitively. A directly uploaded CSV is recognized by its Wi-Fi plot header even when the upload service renames the stored file.
- Combined L3/Event ZIP upload and existing remote/cloud diagnostic ZIP import: these already call `ZipImportService`; it now processes `WiFi_plot.csv` alongside NetworkLog/sub-session entries.
- Built-in RF S3 report worker: after downloading the accepted ZIP and running existing archive validation/PDF generation, it imports companion Wi-Fi plots before publishing the PDF/report completion. The existing upload's session, company and IN/TW region are used; ownership is rechecked. No extra ZIP processing is added to the upload registration or `/complete` request.

All three paths share `WifiPlotCsv` and `WifiPlotImporter`. They write `tbl_network_log` using the current regional connection. No new table, schema migration or frontend endpoint is needed. The external reporting mode does not run the built-in RF worker.

## Stored data

Each valid timestamp becomes a network-log row linked to the authorized target session/company. CSV `session` is retained as source metadata and cannot select the DB session. `network` is `WiFi`; `primary` is `Yes`.

- `timestamp`, `lat`, `lon` use captured time/GPS. Missing GPS stays null; x/y plot coordinates are not substituted for latitude/longitude.
- `rssi`, `level`, `band`, `channel`, `dl_tpt`, `ul_tpt`, SSID and BSSID columns receive available Wi-Fi readings.
- `primary_cell_info_1` starts with `SSID: ...` / `BSSID: ...` and retains all supplied CSV columns, including plot x/y, link speeds, frequency, lock state, tap/drip timestamps, interpolation/zoom settings and source session.
- Colour suffixes such as `-33@#000000` and `5 GHz@#1565C0` are stripped from metric/band values. Valid zeroes are retained. An `@`/empty reading remains missing.
- Subsecond source time remains in `plot_timestamp` even where the existing MySQL DATETIME column rounds/truncates milliseconds. Comment/footer rows such as `# COUNTS: Tapped ...` are ignored. Other invalid timestamps are counted, without generating a fake measurement.

The existing GetNetworkLog parser returns the saved values as flat `wifi_*` fields. See [Wi-Fi API fields](get-network-log-wifi.md). It decodes quoted values once; an actual SSID named `null` remains distinct from a missing SSID. Raw primary-cell text remains hidden and is not duplicated in `extra_json`.

## Performance and consistency

- Input is streamed; the full CSV/ZIP is not materialized by the Wi-Fi importer.
- Parameterized multi-row inserts use at most 500 records per batch and a conservative approximately 1 MiB payload budget. There is no DB lookup/insert per CSV row.
- One target-session lock verifies company ownership and serializes concurrent Wi-Fi imports to that session. Batch lookups compare stable source hashes, so repeat import of the same plot does not insert it again. The deduplication is scoped to the regional target session; separate authorized sessions retain their own records.
- Existing manual upload transactions remain caller-owned; their rollback also removes Wi-Fi rows. Standalone cloud/S3 Wi-Fi file imports use an owned transaction. A temporary-table regression verifies both modes.
- Existing GetNetworkLog page limits, cursors, version checks and caching remain. Wi-Fi scan rows are exempt from the cellular band-presence gate so plot rows with a missing band can appear. The cache namespace is bumped to `networklog:v39`. No new SQL lookups are performed per returned API row.
- API latency under a live authenticated production workload has not been benchmarked for this change. Import/read query count and existing cache behavior are checked; increased JSON size depends on the Wi-Fi fields actually present.

## Verification

The provided 260,468-byte CSV snapshot contains 979 timestamped records plus a counts footer. Of those records, 926 have missing GPS and are retained without fabricated coordinates. Parsing and source-to-API checks passed.

Real MySQL checks on the configured India and Taiwan connections used connection-local temporary shadows of the network/session/user tables. They copied the actual network/session column types/defaults/indexes, imported all 979 records, verified zero new inserts on repeat import, exercised the actual GetNetworkLog SQL filter, checked saved source parsing, and confirmed caller rollback. No permanent application rows or schema were modified by those tests. Cross-company targets are rejected.

```powershell
dotnet build CallAnalyzerRegression/CallAnalyzerRegression.csproj --no-restore -p:OutputPath=buildcheck_wifi_plot/ -p:UseAppHost=false
dotnet CallAnalyzerRegression/buildcheck_wifi_plot/CallAnalyzerRegression.dll --wifi-plot '<path-to-WiFi_plot.csv>'
dotnet CallAnalyzerRegression/buildcheck_wifi_plot/CallAnalyzerRegression.dll --network-log-cache
dotnet CallAnalyzerRegression/buildcheck_wifi_plot/CallAnalyzerRegression.dll --rf-log-integration
```

An explicit `--wifi-plot-db '<path-to-WiFi_plot.csv>'` mode requires temporary-table privileges and runs the temporary-table checks separately on IN and TW without starting the web host/workers or sending emails.

Deploy/restart the updated backend before testing client requests. Previously completed imports are not silently backfilled; re-import the source ZIP/CSV through the appropriate existing authorized workflow to ingest a previously ignored plot file.
