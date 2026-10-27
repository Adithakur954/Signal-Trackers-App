using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Services;
using StackExchange.Redis;

internal static class NetworkLogCacheRegression
{
    internal static async Task RunAsync()
    {
        foreach (var name in new[] { "GetNetworkLogDataVersionAsync", "GetMainDataOnlyRaw", "GetAppSummaryRaw", "GetCombinedStatsRaw" })
        {
            var parameters = typeof(MapViewController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetParameters();
            Check(parameters.Any(parameter => parameter.ParameterType == typeof(MySqlConnection) && !parameter.IsOptional)
                && parameters.Any(parameter => parameter.ParameterType == typeof(MySqlTransaction) && !parameter.IsOptional),
                name + " must receive the request's shared read connection and transaction");
        }
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql("Server=localhost;Database=MciSchemaRegression;User ID=unused;",
                new MySqlServerVersion(new Version(8, 0, 29))).Options;
        using (var context = new ApplicationDbContext(options))
        {
            foreach (var entity in new[] { typeof(tbl_network_log), typeof(tbl_network_log_neighbour) })
            {
                var property = context.Model.FindEntityType(entity)!.FindProperty("mci")!;
                Check(property.GetColumnType() == "text" && property.IsNullable,
                    entity.Name + ".mci must support longer values without truncation");
            }
        }
        var time = new TestTime();
        using var cache = new VersionedNetworkLogCache(100, time);
        var builds = 0;
        async Task<MapViewController.NetworkLogFullResponse> Build()
        {
            Interlocked.Increment(ref builds);
            await Task.Delay(5);
            return Sample();
        }
        Task<VersionedNetworkLogCache.Result<MapViewController.NetworkLogFullResponse>> Fetch(
            string key = "db-a:version-1:page-1", RedisService? redis = null, bool bypass = false)
            => cache.GetOrCreateAsync(key, redis, Build, value => value.data.Count, bypass);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Fetch()));
        Check(builds == 1 && results.Count(item => !item.Hit) == 1,
            "Concurrent requests must share the build and cache publication");
        Check(results.Last().Store == "MEMORY", "Redis outage must use bounded local cache");
        await Fetch("db-a:version-2:page-1");
        await Fetch("db-b:version-1:page-1");
        await Fetch("db-a:version-1:page-2");
        Check(builds == 4, "Database/version/page scopes were mixed");
        await Fetch(bypass: true);
        Check(builds == 5, "Force refresh did not rebuild");
        cache.Invalidate();
        await Fetch();
        Check(builds == 6, "Mutation did not invalidate offline cache");
        time.Now = time.Now.AddMinutes(6);
        await Fetch();
        Check(builds == 7, "Expired local response was reused");

        var payloads = new ConcurrentDictionary<string, RedisValue>();
        var writes = 0;
        var reads = 0;
        var connected = true;
        var stall = false;
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Proxy<IDatabase>((method, args) => method.Name switch
        {
            "StringGetAsync" => Read(args!),
            "StringSetAsync" => Store(args!),
            _ => throw new NotSupportedException(method.Name)
        });
        Task<RedisValue> Read(object?[] args)
        {
            reads++;
            return stall ? pending.Task : Task.FromResult(payloads.GetValueOrDefault(((RedisKey)args[0]!).ToString(), RedisValue.Null));
        }
        Task<bool> Store(object?[] args)
        {
            writes++;
            payloads[((RedisKey)args[0]!).ToString()] = (RedisValue)args[1]!;
            return Task.FromResult(true);
        }
        var mux = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "get_IsConnected" => connected,
            "GetDatabase" => database,
            _ => throw new NotSupportedException(method.Name)
        });
        var redis = new RedisService(mux);
        var cold = await Fetch("redis-page", redis);
        var warm = await Fetch("redis-page", redis);
        Check(!cold.Hit && warm.Hit && warm.Store == "REDIS" && writes == 1,
            "A page must be written once, and read back without rebuilding");
        Check(ResponseJson(cold.Value) == ResponseJson(warm.Value),
            "Redis serialization changed response fields or calculation values");
        var stored = await redis.GetObjectAsync<VersionedNetworkLogCache.Entry<MapViewController.NetworkLogFullResponse>>("redis-page");
        var expiresAt = stored!.ExpiresAtUtc;
        await Fetch("redis-page", redis);
        var reread = await redis.GetObjectAsync<VersionedNetworkLogCache.Entry<MapViewController.NetworkLogFullResponse>>("redis-page");
        Check(writes == 1 && reread!.ExpiresAtUtc == expiresAt, "A cache hit extended absolute expiration");
        time.Now = expiresAt.AddSeconds(1);
        var expired = await Fetch("redis-page", redis);
        Check(!expired.Hit && writes == 2, "Expired Redis envelope was accepted");
        var before = builds;
        payloads.TryRemove("redis-page", out _);
        await Fetch("redis-page", redis);
        Check(builds == before + 1, "Real Redis miss resurrected an invalidated local response");
        connected = false;
        Check((await Fetch("redis-page", redis)).Store == "MEMORY", "Disconnected Redis ignored local page");
        connected = true;
        stall = true;
        Check((await Fetch("redis-page", redis)).Store == "MEMORY", "Redis timeout discarded local page");
        var previousReads = reads;
        await Fetch("redis-page", redis);
        Check(reads == previousReads, "Redis timeout cooldown was not respected");
        pending.TrySetResult(RedisValue.Null);

        await CheckInvalidationDuringBuildAsync();
        await CheckFailedBuildAsync();
        using (var bounded = new VersionedNetworkLogCache(1))
        {
            await bounded.GetOrCreateAsync("oversized", null, () => Task.FromResult(Sample()), _ => 2);
            var oversized = await bounded.GetOrCreateAsync("oversized", null, () => Task.FromResult(Sample()), _ => 2);
            Check(!oversized.Hit, "Oversized page exceeded local cache size limit");
        }
        CheckJsonFallbackCalculations();
        CheckWifiFields();
        Console.WriteLine("Network log cache: single build/write, version isolation, force refresh, expiry, invalidation, Redis fallback and JSON/calculation compatibility passed.");
    }

    private static async Task CheckInvalidationDuringBuildAsync()
    {
        using var cache = new VersionedNetworkLogCache(10);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetOrCreateAsync("race", null, async () =>
        {
            entered.SetResult();
            await release.Task;
            return Sample();
        }, _ => 1);
        await entered.Task;
        cache.Invalidate();
        release.SetResult();
        await first;
        var next = await cache.GetOrCreateAsync("race", null, () => Task.FromResult(Sample()), _ => 1);
        Check(!next.Hit, "An in-flight old build repopulated the invalidated local cache");
    }

    private static async Task CheckFailedBuildAsync()
    {
        using var cache = new VersionedNetworkLogCache(10);
        try
        {
            await cache.GetOrCreateAsync<MapViewController.NetworkLogFullResponse>("failure", null,
                () => throw new InvalidOperationException("expected"), _ => 1);
            throw new Exception("Failed factory was swallowed");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected") { }
        var next = await cache.GetOrCreateAsync("failure", null, () => Task.FromResult(Sample()), _ => 1);
        Check(!next.Hit, "Failed build leaked a gate or cached an error");
    }

    private static MapViewController.NetworkLogFullResponse Sample() => new()
    {
        data = new()
        {
            new() { id = 42, session_id = 7, timestamp = new DateTime(2026, 7, 24, 14, 53, 38),
                lat = 7.066433f, lon = -8.873192f, rsrp = -69, dl_tpt = "0.00", ul_tpt = "0.00",
                network = "2G", band = "DCS 1800", extra_json = "{}" }
        },
        app_summary = new() { ["Whatsapp"] = new { sample_count = 3, avgRsrp = -69.25 } },
        io_summary = new() { ["Outdoor"] = new { inputCount = 2, outputCount = 3 } },
        tpt_volume = new { dl_kb = 321.125, ul_kb = 12.5 },
        TotalCount = 3,
        Sessions = new() { new() { SessionId = 7, Count = 3 } },
        CachedAt = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc),
        DataVersion = "3-42-0-123"
    };

    private static string ResponseJson(MapViewController.NetworkLogFullResponse model)
    {
        var method = typeof(MapViewController).GetMethod("ToNetworkLogResponseObject", BindingFlags.Static | BindingFlags.NonPublic)!;
        var response = JsonSerializer.SerializeToElement(method.Invoke(null, new object[] { model, 1, "HIT" }));
        var expected = new[] { "data", "app_summary", "io_summary", "tpt_volume", "total_count", "session_count",
            "sessions", "cachedAt", "cache_state", "data_version", "next_cursor_timestamp", "next_cursor_id" };
        Check(response.EnumerateObject().Select(property => property.Name).SequenceEqual(expected), "Response JSON contract changed");
        Check(response.GetProperty("total_count").GetInt32() == 3 && response.GetProperty("next_cursor_id").GetInt32() == 42,
            "Counts or cursor changed");
        return response.GetRawText();
    }

    private static void CheckJsonFallbackCalculations()
    {
        var method = typeof(MapViewController).GetMethod("ApplyNetworkLogExtraJsonFallbacks", BindingFlags.Static | BindingFlags.NonPublic)!;
        var download = typeof(MapViewController).GetMethod("ApplyNetworkLogDownloadSize", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string nested = """{"altitude":"37.63","network_log_fields":{"DL THPT":"25.27","ENDC Setup":"Yes","PS App DL (Mbps)":"26.45","PS App UL (Mbps)":"0.18","PUSCH Tx (dBm)":"-17 dBm","UL THPT":"0.18"},"phone_heading":"202.0","ps":"{\u0027direction\u0027:\u0027Receive\u0027;\u0027duration_ms\u0027:56670;\u0027speed_kbps\u0027:9034.848254560393;\u0027file_size_bytes\u0027:524292967;\u0027down_bytes\u0027:524292967;\u0027up_bytes\u0027:0;\u0027result_status\u0027:\u0027SUCCESS\u0027}","sub_session_id":"20"}""";
        foreach (var encoded in new[] { false, true })
        {
            var row = new MapViewController.NetworkLogCacheRow { extra_json = encoded ? JsonSerializer.Serialize(nested) : nested };
            download.Invoke(null, new object[] { row });
            method.Invoke(null, new object[] { row });
            Check(row.dl_tpt == "26.45" && row.ul_tpt == "0.18", "Nested CSV throughput must use the existing response fields");
            using var responseExtra = JsonDocument.Parse(row.extra_json);
            Check(!responseExtra.RootElement.TryGetProperty("network_log_fields", out _)
                && row.extra_json_data.Count == 10
                && (string?)row.extra_json_data["ps_app_dl_mbps"] == "26.45"
                && (string?)row.extra_json_data["ENDC Setup"] == "Yes"
                && (string?)row.extra_json_data["pusch_tx"] == "-17 dBm"
                && !row.extra_json_data.ContainsKey("PS App DL (Mbps)")
                && !row.extra_json_data.ContainsKey("PS App UL (Mbps)")
                && !row.extra_json_data.ContainsKey("PUSCH Tx (dBm)"),
                "New exports must use legacy keys so the frontend shows one option per metric");
            Check(JsonSerializer.Serialize(row.extra_json_data) == JsonSerializer.Serialize(responseExtra.RootElement),
                "extra_json and extra_json_data must expose the same flat fields");
            foreach (var serialized in new[] { JsonSerializer.Serialize(row), Newtonsoft.Json.JsonConvert.SerializeObject(row) })
            {
                using var responseRow = JsonDocument.Parse(serialized);
                Check(responseRow.RootElement.GetProperty("extra_json").GetString() == row.extra_json
                    && !responseRow.RootElement.TryGetProperty("extra_json_data", out _),
                    "API rows must return extra data only once, in the existing extra_json string");
            }
            var normalizedExtra = row.extra_json;
            method.Invoke(null, new object[] { row });
            Check(row.extra_json == normalizedExtra, "Flattening must be idempotent");
            Check(row.downloaded_file_size_bytes == 524292967 && row.download_direction == "Receive"
                && row.download_result_status == "SUCCESS", "Loose PS download details changed for nested exports");
            var flat = new MapViewController.NetworkLogCacheRow
            {
                extra_json = """{"ps_app_dl_mbps":"26.45","ps_app_ul_mbps":"0.18","ps":"{'direction':'Receive';'duration_ms':56670;'down_bytes':524292967;'result_status':'SUCCESS'}"}"""
            };
            download.Invoke(null, new object[] { flat });
            method.Invoke(null, new object[] { flat });
            Check(flat.dl_tpt == row.dl_tpt && flat.ul_tpt == row.ul_tpt
                && flat.downloaded_file_size_4g_bytes == row.downloaded_file_size_4g_bytes,
                "Flat and nested exports must produce identical throughput and download calculations");
        }
        var mixed = new MapViewController.NetworkLogCacheRow
        {
            dl_tpt = "25.27", extra_json = """{"ps_app_dl_mbps":10,"ps_app_ul_mbps":0,"NETWORK_LOG_FIELDS":{"PS App DL (Mbps)":"26.45","PS App UL (Mbps)":"0.18"}}"""
        };
        method.Invoke(null, new object[] { mixed });
        Check(mixed.dl_tpt == "25.27" && mixed.ul_tpt == ""
            && Convert.ToDouble(mixed.extra_json_data["ps_app_dl_mbps"]) == 10,
            "Stored columns and explicit legacy metrics must take precedence over nested headers, including zero");
        var generic = new MapViewController.NetworkLogCacheRow { extra_json = """{"network_log_fields":{"DL THPT":"25.27","UL THPT":"0.18"}}""" };
        method.Invoke(null, new object[] { generic });
        Check(generic.dl_tpt == "25.27" && generic.ul_tpt == "0.18", "Generic CSV throughput must remain available without PS App fields");
        foreach (var raw in new[]
        {
            """{"ps_app_dl_mbps":10,"ps_app_ul_mbps":0,"pusch_tx":"2 dBm"}""",
            """{"PS App DL (Mbps)":10,"PS App UL (Mbps)":0,"PUSCH Tx (dBm)":"2 dBm"}""",
            """{"ps_app_dl_mbps":10,"ps_app_ul_mbps":0,"network_log_fields":{"PS App DL (Mbps)":20,"PS App UL (Mbps)":5,"PUSCH Tx (dBm)":"2 dBm"}}"""
        })
        {
            var aliasRow = new MapViewController.NetworkLogCacheRow { extra_json = raw };
            method.Invoke(null, new object[] { aliasRow });
            using var normalized = JsonDocument.Parse(aliasRow.extra_json);
            Check(normalized.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(
                    new[] { "ps_app_dl_mbps", "ps_app_ul_mbps", "pusch_tx" }.Order())
                && normalized.RootElement.GetProperty("ps_app_dl_mbps").GetInt32() == 10
                && normalized.RootElement.GetProperty("ps_app_ul_mbps").GetInt32() == 0,
                "Old, flat-header and mixed exports must expose identical dropdown keys while preserving canonical values including zero");
        }
        var collision = new MapViewController.NetworkLogCacheRow
        {
            extra_json = """{"ENDC Setup":"No","custom":"","network_log_fields":{"ENDC Setup":"Yes","custom":"captured","other":0},"ps":"unchanged"}"""
        };
        method.Invoke(null, new object[] { collision });
        Check((string?)collision.extra_json_data["ENDC Setup"] == "No"
            && (string?)collision.extra_json_data["custom"] == "captured"
            && (string?)collision.extra_json_data["ps"] == "unchanged"
            && Convert.ToInt64(collision.extra_json_data["other"]) == 0,
            "Flattening must preserve existing values, metadata, unknown captured fields and zero values");
        foreach (var encoded in new[] { false, true })
        {
            const string raw = "{\"ps_app_dl_mbps\":12.34567,\"ps_app_ul_mbps\":3.25,\"disp_rsrp_dbm\":-91,\"disp_level\":2.5,\"nested\":{\"ok\":true},\"items\":[1,null,\"x\"]}";
            var row = new MapViewController.NetworkLogCacheRow { extra_json = encoded ? JsonSerializer.Serialize(raw) : raw,
                dl_tpt = "0.00", ul_tpt = "7.00", rsrq = -12 };
            method.Invoke(null, new object[] { row });
            Check(row.extra_json == (encoded ? JsonSerializer.Serialize(raw) : raw), "Legacy extra_json changed");
            Check(row.dl_tpt == "12.346" && row.ul_tpt == "7.00" && row.rsrp == -91 && row.level == 3 && row.rsrq == -12,
                "JSON fallback precedence or rounding changed");
            Check(row.extra_json_data.Count == 6 && row.extra_json_data.ContainsKey("NESTED"), "Extra JSON shape or case-insensitive keys changed");
        }
        foreach (var raw in new[] { "", "{}", "not-json", "[]", "null" })
        {
            var row = new MapViewController.NetworkLogCacheRow { extra_json = raw, dl_tpt = "0.00", ul_tpt = "5.00" };
            method.Invoke(null, new object[] { row });
            Check(row.dl_tpt == "0.00" && row.ul_tpt == "5.00" && row.rsrp == null && row.extra_json_data.Count == 0,
                "Empty/malformed JSON changed fallback values");
        }
    }

    private static void CheckWifiFields()
    {
        const string scan = """SSID: "Airtel_Vinfocom", BSSID: 0e:36:23:c3:64:7a, capabilities: [WPA2-PSK-CCMP-128][RSN-PSK-CCMP-128][ESS], level: -35, frequency: 5240, timestamp: 5826658044, distance: ?(cm), distanceSd: ?(cm), passpoint: no, ChannelBandwidth: 3, centerFreq0: 5210, centerFreq1: 5250, standard: 11ax, 80211mcResponder: is not supported, 80211azNtbResponder: is not supported, TWT Responder: yes, Radio Chain Infos: [RadioChainInfo: id=0, level=-36, RadioChainInfo: id=1, level=-42], interface name: wlan0""";
        var normalize = typeof(MapViewController).GetMethod("NormalizeNetworkLogRows", BindingFlags.Static | BindingFlags.NonPublic)!;
        var row = new MapViewController.NetworkLogCacheRow
        {
            connection_type = "wifi", primary_cell_info_1 = scan,
            rssi = -50, level = 3, extra_json = """{"custom":"unchanged"}"""
        };
        normalize.Invoke(null, new object[] { new List<MapViewController.NetworkLogCacheRow> { row } });
        Check(row.is_wifi && row.log_type == "wifi" && row.wifi_ssid == "Airtel_Vinfocom"
            && row.wifi_bssid == "0e:36:23:c3:64:7a" && row.wifi_rssi_dbm == -35
            && row.wifi_frequency_mhz == 5240 && row.wifi_timestamp == 5826658044L,
            "Wi-Fi normalization must expose the source SSID, BSSID, signal, frequency and 64-bit timestamp");
        Check(row.wifi_capabilities == "[WPA2-PSK-CCMP-128][RSN-PSK-CCMP-128][ESS]"
            && row.wifi_distance == "?(cm)" && row.wifi_distance_sd == "?(cm)"
            && row.wifi_passpoint == false && row.wifi_channel_bandwidth == 3
            && row.wifi_center_freq0_mhz == 5210 && row.wifi_center_freq1_mhz == 5250
            && row.wifi_standard == "11ax" && row.wifi_80211mc_responder == false
            && row.wifi_80211az_ntb_responder == false && row.wifi_twt_responder == true
            && row.wifi_interface_name == "wlan0", "All Wi-Fi scan details must retain their captured values");
        Check(row.wifi_radio_chain_infos is { Count: 2 } chains
            && chains[0].id == 0 && chains[0].level == -36 && chains[1].id == 1 && chains[1].level == -42,
            "Radio-chain commas must not split the outer Wi-Fi fields");
        Check(row.rssi == -50 && row.level == 3 && row.extra_json == """{"custom":"unchanged"}""",
            "Wi-Fi extraction must not overwrite existing DB metrics or extra_json");
        foreach (var json in new[] { JsonSerializer.Serialize(row), Newtonsoft.Json.JsonConvert.SerializeObject(row) })
        {
            using var response = JsonDocument.Parse(json);
            Check(response.RootElement.EnumerateObject().Count(p => p.Name.StartsWith("wifi_", StringComparison.Ordinal)) == 18
                && !response.RootElement.TryGetProperty("primary_cell_info_1", out _)
                && !response.RootElement.TryGetProperty("extra_json_data", out _)
                && !response.RootElement.GetProperty("wifi_passpoint").GetBoolean()
                && response.RootElement.GetProperty("wifi_radio_chain_infos").GetArrayLength() == 2,
                "Both JSON serializers must expose each Wi-Fi field once, including false booleans, without the raw diagnostic text");
        }
        var cached = JsonSerializer.Deserialize<MapViewController.NetworkLogCacheRow>(JsonSerializer.Serialize(row))!;
        Check(cached.wifi_timestamp == 5826658044L && cached.wifi_radio_chain_infos?[1].level == -42
            && cached.wifi_passpoint == false && cached.primary_cell_info_1 == "",
            "Wi-Fi values must survive cache serialization even though raw diagnostic text is ignored");
        var firstJson = JsonSerializer.Serialize(row);
        normalize.Invoke(null, new object[] { new List<MapViewController.NetworkLogCacheRow> { row } });
        Check(JsonSerializer.Serialize(row) == firstJson, "Repeated Wi-Fi normalization must be idempotent");

        var quoted = new WifiNetworkLogFields();
        WifiScanInfoParser.Apply(quoted, """SSID: "Office, \"Guest\": 5G", BSSID: aa:bb:cc:dd:ee:ff, Radio Chain Infos: [], interface name: wlan1""");
        Check(quoted.wifi_ssid == "Office, \"Guest\": 5G" && quoted.wifi_bssid == "aa:bb:cc:dd:ee:ff"
            && quoted.wifi_radio_chain_infos is { Count: 0 } && quoted.wifi_interface_name == "wlan1",
            "Quoted SSIDs may contain commas, escaped quotes and colons; explicitly empty radio chains must remain empty");
        var partial = new WifiNetworkLogFields();
        WifiScanInfoParser.Apply(partial, "ssid: Bob's Office, FREQUENCY: NaN, timestamp: 999999999999999999999999, passpoint: unknown, ChannelBandwidth: invalid, interfaceName: wlan0");
        Check(partial.wifi_ssid == "Bob's Office" && partial.wifi_frequency_mhz == null
            && partial.wifi_timestamp == null && partial.wifi_passpoint == null
            && partial.wifi_channel_bandwidth == null && partial.wifi_interface_name == "wlan0",
            "Partial/case-insensitive scans and malformed numbers must be handled without fabricated zero/false values");
        foreach (var json in new[] { JsonSerializer.Serialize(partial), Newtonsoft.Json.JsonConvert.SerializeObject(partial) })
        {
            using var response = JsonDocument.Parse(json);
            Check(!response.RootElement.TryGetProperty("wifi_frequency_mhz", out _)
                && !response.RootElement.TryGetProperty("wifi_passpoint", out _), "Missing Wi-Fi values must be omitted");
        }

        const string cellular = "CellInfoNr:{ mRegistered=YES CellIdentityNr:{ mPci = 40 mNrArfcn = 528750 } CellSignalStrengthNr:{ ssRsrp = -95 ssRsrq = -12 ssSinr = 9 level = 1 } }";
        foreach (var connectionType in new[] { "network", "wifi" })
        {
            var cell = new MapViewController.NetworkLogCacheRow { connection_type = connectionType, primary_cell_info_1 = cellular };
            normalize.Invoke(null, new object[] { new List<MapViewController.NetworkLogCacheRow> { cell } });
            foreach (var json in new[] { JsonSerializer.Serialize(cell), Newtonsoft.Json.JsonConvert.SerializeObject(cell) })
            {
                using var response = JsonDocument.Parse(json);
                Check(!response.RootElement.EnumerateObject().Any(p => p.Name.StartsWith("wifi_", StringComparison.Ordinal)),
                    "Cellular diagnostics must not become Wi-Fi scan details, even inside a Wi-Fi session");
            }
        }
        Console.WriteLine("Network log Wi-Fi: complete scan fields, quoted SSIDs, radio chains, unknown values, cellular exclusion and cache/JSON compatibility passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = default!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
