# GetNetworkLog Wi-Fi response fields

Both GET and POST `/api/MapView/GetNetworkLog` expose captured Wi-Fi scan details as additional `wifi_*` properties directly on each Wi-Fi row. The source is `tbl_network_log.primary_cell_info_1`; a separate Wi-Fi table is not required. Existing login, filtering, pagination and regional DB selection continue to apply.

For the supplied scan, the added response properties are:

```json
{
  "wifi_ssid": "Airtel_Vinfocom",
  "wifi_bssid": "0e:36:23:c3:64:7a",
  "wifi_capabilities": "[WPA2-PSK-CCMP-128][RSN-PSK-CCMP-128][ESS]",
  "wifi_rssi_dbm": -35,
  "wifi_frequency_mhz": 5240,
  "wifi_timestamp": 5826658044,
  "wifi_distance": "?(cm)",
  "wifi_distance_sd": "?(cm)",
  "wifi_passpoint": false,
  "wifi_channel_bandwidth": 3,
  "wifi_center_freq0_mhz": 5210,
  "wifi_center_freq1_mhz": 5250,
  "wifi_standard": "11ax",
  "wifi_80211mc_responder": false,
  "wifi_80211az_ntb_responder": false,
  "wifi_twt_responder": true,
  "wifi_radio_chain_infos": [
    { "id": 0, "level": -36 },
    { "id": 1, "level": -42 }
  ],
  "wifi_interface_name": "wlan0"
}
```

These properties accompany the existing row fields, including `is_wifi`, `connection_type`, `log_type`, `rssi`, `level` and `extra_json`. Existing DB metrics are not overwritten by the scan's signal value. The complete raw `primary_cell_info_1` remains hidden, and the new details are not duplicated in `extra_json`.

Absent or unreadable values are omitted rather than invented. Captured `false`, zero, empty SSIDs and empty radio-chain arrays are retained. Distance strings, the timestamp value, the bandwidth enum and the standard text preserve the source representation; `wifi_channel_bandwidth: 3` is the captured enum, not a claim of 3 MHz bandwidth. Cellular `CellInfoNr` diagnostics do not generate Wi-Fi fields, even when the session itself is marked Wi-Fi.

The page/summary cache namespace is now `networklog:v39`, so responses cached under the previous contract do not hide the new properties. No frontend or database schema changes are required for this backend addition; a frontend that explicitly selects columns can map the added keys when displaying Wi-Fi details.

## WiFi_plot.csv imports

`WiFi_plot.csv` is now recognized by manual network uploads, combined L3/Event ZIP uploads, the existing remote/cloud ZIP import path, and the built-in S3 RF report worker. See [Wi-Fi plot import](wifi-plot-import.md) for mapping, batching, regional scope and verification. The plot's readings and metadata are stored in `tbl_network_log.primary_cell_info_1`; the API uses the same parser as for captured Android scan strings.

Additional plot-specific keys include `wifi_link_speed_mbps`, `wifi_rx_link_speed_mbps`, `wifi_channel`, `wifi_band`, `wifi_signal_level_0_4`, `wifi_matches_lock`, `wifi_lock`, `wifi_plot_x`, `wifi_plot_y`, `wifi_plot_timestamp`, `wifi_tap_timestamp`, `wifi_drip_timestamp`, `wifi_idle_before_s`, `wifi_interval_s`, `wifi_interpolation_on`, `wifi_zoom`, `wifi_source_session`, `wifi_pin_size` and `wifi_nodata_mode`. The source session is metadata; `session_id` remains the authorized DB session. Existing root `dl_tpt`/`ul_tpt` contain cleaned throughput values.

Wi-Fi plot rows remain eligible even when band/signal readings are blank. Rows with missing GPS remain stored with null coordinates; a session still needs at least one mappable GPS row for the map endpoint. Unknown scan-only fields such as standard, capabilities or TWT are not invented when the plot CSV does not contain them.

## Mixed cellular and Wi-Fi rows in one session

Existing connection classification also accepts `tbl_session.type = 'wifi'`. Consequently, a cellular diagnostic row in a Wi-Fi session can have `is_wifi: true` without containing an SSID/BSSID scan. The new `wifi_*` properties describe that row's own `primary_cell_info_1`; the backend does not copy a later scan onto an earlier cellular sample.

On 27 October 2026, a read-only check of the actual `GetMainDataOnlyRaw` query and normalization path for India session `8303`, with `NetworkType=All`, returned 215 rows, including 209 parsed Wi-Fi scans. Row `12611246` at `2026-10-26T15:10:21` contains `CellInfoLte`. Row `12611260` at `2026-10-26T15:20:57` contains `SSID: "Airtel_Vinfocom"` and produced all 18 Wi-Fi fields. This check verifies the current source against the configured DB, not deployment of that source to a running server.

To inspect that session with the existing login, include the actual scan times and avoid a cellular-only technology filter:

```http
GET /api/MapView/GetNetworkLog?session_ids=8303&NetworkType=All&limit=1000&force_refresh=true
```

Look for row `12611260` / `wifi_ssid` in the response. The first cellular rows need not contain `wifi_*` fields. A narrow date/time window, a small page limit or a cellular-only technology selection can prevent the actual scan rows from appearing on the viewed page.
