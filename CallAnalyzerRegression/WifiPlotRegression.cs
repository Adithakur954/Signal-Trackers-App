using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class WifiPlotRegression
{
    private const string Sample = """
timestamp,lat,lon,x,y,WiFi RSSI,Link Speed,Channel,Band,DL THPT,UL THPT,ssid,bssid,rx_link_speed_mbps,frequency_mhz,signal_level_0_4,matches_lock,lock,tap_ts,drip_ts,idle_before_s,interval_s,interp_on,zoom,session,pin_size,nodata_mode
2026-10-22 14:43:06.610,,,2551.2417,4341.589,-33@#000000,325@#006400,48@#F23030,5 GHz@#1565C0,0.73@#FF0000,39.56@#006100,Airtel_Vinfocom,0e:36:23:c3:64:7a,433,5240,4,yes,Airtel_Vinfocom,2026-10-22 14:43:09.815,2026-10-22 14:43:09.944,,0.10,1,9X@9720,8288,X-Large,gap
2026-10-22 14:43:03.414,28.6293517,77.3802493,2554.1162,4330.1025,@,@,@,@,5.81@#80FF00,65.20@#006100,,,,,,,Airtel_Vinfocom,2026-10-22 14:43:03.436,2026-10-22 14:43:03.437,,1.00,1,9X@9720,8288,X-Large,gap
""";

    internal static void Run(string? samplePath)
    {
        var invalid = 0;
        var logs = WifiPlotCsv.Read(new StringReader(Sample + "\nnot-a-date,\n"), 17, 23, () => invalid++).ToList();
        Check(logs.Count == 2 && invalid == 1, "Timestamp validation must retain valid plot rows.");
        var log = logs[0];
        Check(log.session_id == 17 && log.company_id == 23 && log.network == "WiFi" && log.primary == "Yes",
            "Use the authorized session/company, independently of the CSV's source session.");
        Check(log.lat == null && log.lon == null && log.rssi == -33 && log.level == 4
            && log.channel == "48" && log.band == "5 GHz" && log.dl_tpt == "0.73" && log.ul_tpt == "39.56",
            "Strip metric colours without fabricating GPS or losing throughput.");
        var api = Normalize(log);
        Check(api.wifi_ssid == "Airtel_Vinfocom" && api.wifi_bssid == "0e:36:23:c3:64:7a"
            && api.wifi_frequency_mhz == 5240 && api.wifi_rssi_dbm == -33 && api.wifi_channel == 48
            && api.wifi_band == "5 GHz" && api.wifi_link_speed_mbps == 325 && api.wifi_rx_link_speed_mbps == 433
            && api.wifi_signal_level_0_4 == 4 && api.wifi_matches_lock == true && api.wifi_lock == "Airtel_Vinfocom",
            "Stored primary-cell text must produce the Wi-Fi plot API metrics.");
        Check(api.wifi_plot_x == 2551.2417 && api.wifi_plot_y == 4341.589 && api.wifi_source_session == "8288"
            && api.wifi_plot_timestamp == "2026-10-22 14:43:06.610" && api.wifi_interval_s == .1
            && api.wifi_interpolation_on == true && api.wifi_zoom == "9X@9720" && api.wifi_pin_size == "X-Large"
            && api.wifi_nodata_mode == "gap" && api.wifi_tap_timestamp == "2026-10-22 14:43:09.815"
            && api.wifi_drip_timestamp == "2026-10-22 14:43:09.944", "Preserve plot metadata and subsecond source times.");
        var missing = Normalize(logs[1]);
        Check(missing.is_wifi && missing.wifi_ssid == null && missing.wifi_bssid == null && missing.wifi_rssi_dbm == null
            && missing.wifi_link_speed_mbps == null && missing.lat.HasValue && missing.dl_tpt == "5.81",
            "A missing reading stays missing while valid GPS/throughput remains available.");
        foreach (var json in new[] { JsonSerializer.Serialize(api), Newtonsoft.Json.JsonConvert.SerializeObject(api) })
        {
            using var value = JsonDocument.Parse(json);
            Check(value.RootElement.GetProperty("wifi_channel").GetInt32() == 48
                && !value.RootElement.TryGetProperty("primary_cell_info_1", out _), "API output stays flat and raw source stays hidden.");
        }
        var persisted = JsonSerializer.Deserialize<MapViewController.NetworkLogCacheRow>(JsonSerializer.Serialize(api))!;
        Check(persisted.wifi_link_speed_mbps == 325 && persisted.wifi_plot_timestamp == api.wifi_plot_timestamp,
            "Plot metrics must survive response-cache serialization.");
        var nullName = new WifiNetworkLogFields();
        WifiScanInfoParser.Apply(nullName, "SSID: \"null\", BSSID: null");
        Check(nullName.wifi_ssid == "null" && nullName.wifi_bssid == null, "Literal SSID null differs from a missing SSID.");
        var quoteName = new WifiNetworkLogFields();
        WifiScanInfoParser.Apply(quoteName, "SSID: \"\\\"Guest\\\"\"");
        Check(quoteName.wifi_ssid == "\"Guest\"", "Decode SSID quoting exactly once.");
        Check(WifiPlotCsv.IsFile("nested/WiFi_plot.CSV") && !WifiPlotCsv.IsFile("NetworkLog_test.csv"), "Recognize the companion file precisely.");
        if (!string.IsNullOrWhiteSpace(samplePath))
        {
            var watch = Stopwatch.StartNew();
            using var reader = new StreamReader(samplePath);
            var sample = WifiPlotCsv.Read(reader, 17, 23).ToList();
            Check(sample.Count > 0 && sample.Any(row => Normalize(row).wifi_link_speed_mbps != null), "Real input sample must generate Wi-Fi metrics.");
            Check(WifiPlotCsv.HasHeader(samplePath), "Recognize Wi-Fi plot columns even after an upload filename is changed.");
            Console.WriteLine($"Actual CSV: {sample.Count} parsed rows; {sample.Count(row => !row.lat.HasValue || !row.lon.HasValue)} rows retain missing GPS; parse/normalization check {watch.ElapsedMilliseconds} ms.");
        }
        Console.WriteLine("Wi-Fi plot regressions passed: colour stripping, GPS/unknown preservation, primary-cell storage, API fields, metadata, quoting and cache JSON.");
    }

    internal static async Task RunDatabaseAsync(string? samplePath)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true).AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", "SignalTracker.LocalDevelopment", "secrets.json"), optional: true).Build();
        var source = string.IsNullOrWhiteSpace(samplePath) ? Sample : await File.ReadAllTextAsync(samplePath);
        var expected = WifiPlotCsv.Read(new StringReader(source), 1, 7).Select(row => row.primary_cell_info_1).Distinct().Count();
        foreach (var region in new[] { "IN", "TW" })
        {
            var value = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region));
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{region} DB is not configured.");
            await using var connection = new MySqlConnection(value);
            await connection.OpenAsync();
            // Connection-local temporary shadows only. No permanent rows, schema or mail queues are changed.
            foreach (var table in new[] { "tbl_session", "tbl_network_log" })
            {
                string definition;
                using (var readDefinition = new MySqlCommand($"SHOW CREATE TABLE `{table}`", connection))
                {
                    await using var original = await readDefinition.ExecuteReaderAsync();
                    Check(await original.ReadAsync(), "Source table schema must exist.");
                    definition = original.GetString(1);
                }
                Check(definition.StartsWith($"CREATE TABLE `{table}`", StringComparison.Ordinal), "Unexpected table schema.");
                definition = "CREATE TEMPORARY TABLE" + definition["CREATE TABLE".Length..];
                // MySQL forbids temporary-table foreign keys; retain actual columns, types, defaults and indexes.
                definition = Regex.Replace(definition, @"(?m)^\s*CONSTRAINT[^\r\n]*FOREIGN KEY[^\r\n]*\r?\n", "");
                definition = Regex.Replace(definition, @",(\s*\)\s*ENGINE)", "$1");
                Check(!definition.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase), "Fixture must not reference persistent rows.");
                using var create = new MySqlCommand(definition, connection);
                await create.ExecuteNonQueryAsync();
                using var verify = new MySqlCommand($"SHOW CREATE TABLE `{table}`", connection);
                await using var schema = await verify.ExecuteReaderAsync();
                Check(await schema.ReadAsync() && schema.GetString(1).Contains("CREATE TEMPORARY TABLE", StringComparison.OrdinalIgnoreCase),
                    "Only verified temporary tables may be written by this check.");
            }
            using (var user = new MySqlCommand("CREATE TEMPORARY TABLE tbl_user(id INT PRIMARY KEY,company_id INT); INSERT INTO tbl_user VALUES(1,7),(2,8)", connection))
                await user.ExecuteNonQueryAsync();
            using (var session = new MySqlCommand("INSERT INTO tbl_session(id,user_id,type) VALUES(1,1,'network'),(2,1,'network'),(3,2,'network')", connection))
                await session.ExecuteNonQueryAsync();
            var watch = Stopwatch.StartNew();
            var first = await WifiPlotImporter.ImportAsync(connection, null, new StringReader(source), 1, 7);
            var retry = await WifiPlotImporter.ImportAsync(connection, null, new StringReader(source), 1, 7);
            Check(first.Inserted == expected && retry.Inserted == 0 && retry.Duplicates >= expected,
                "Real MySQL bulk import/retry must insert each plot record once, including subsecond timestamps.");
            var (where, parameters) = new NetworkLogDataService().BuildNetworkLogSqlWhere([1], null, "All", null, null);
            using (var count = new MySqlCommand("SELECT COUNT(*) FROM tbl_network_log WHERE " + where, connection))
            {
                foreach (var parameter in parameters) count.Parameters.AddWithValue(parameter.Key, parameter.Value);
                Check(Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == expected,
                    "GetNetworkLog's actual base filter must include Wi-Fi plot records with missing band/readings.");
            }
            using (var read = new MySqlCommand("SELECT primary_cell_info_1 FROM tbl_network_log WHERE session_id=1 LIMIT 1", connection))
            {
                var stored = (string)(await read.ExecuteScalarAsync())!;
                var fields = new WifiNetworkLogFields();
                WifiScanInfoParser.Apply(fields, stored);
                Check(fields.wifi_source_session != null && fields.wifi_plot_timestamp != null, "DB round-trip must retain source plot details.");
            }
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await WifiPlotImporter.ImportAsync(connection, tx, new StringReader(source), 2, 7);
                await tx.RollbackAsync();
            }
            using (var count = new MySqlCommand("SELECT COUNT(*) FROM tbl_network_log WHERE session_id=2", connection))
                Check(Convert.ToInt32(await count.ExecuteScalarAsync()) == 0, "Caller rollback must remove Wi-Fi inserts with the rest of the upload.");
            try
            {
                await WifiPlotImporter.ImportAsync(connection, null, new StringReader(source), 3, 7);
                throw new Exception("Cross-company Wi-Fi import was accepted.");
            }
            catch (InvalidDataException) { }
            Console.WriteLine($"{region}: real MySQL temporary-table test passed: {first.Inserted} bulk rows, retry deduplication, API SQL filter, source round-trip and caller rollback ({watch.ElapsedMilliseconds} ms). Permanent application data unchanged.");
        }
    }

    private static MapViewController.NetworkLogCacheRow Normalize(tbl_network_log log)
    {
        var row = new MapViewController.NetworkLogCacheRow { session_id = log.session_id ?? 0, timestamp = log.timestamp,
            lat = log.lat, lon = log.lon, connection_type = "wifi", network = log.network ?? "",
            band = log.band ?? "", primary_cell_info_1 = log.primary_cell_info_1 ?? "", rssi = log.rssi,
            level = log.level, dl_tpt = log.dl_tpt ?? "", ul_tpt = log.ul_tpt ?? "" };
        typeof(MapViewController).GetMethod("NormalizeNetworkLogRows", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new List<MapViewController.NetworkLogCacheRow> { row }]);
        return row;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
