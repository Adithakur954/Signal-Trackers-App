using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class NetworkDashboardFallbackRegression
{
    internal static void Run(string output)
    {
        void Check(bool test, string reason) { if (!test) throw new Exception("Network fallback: " + reason); }
        NetworkDashboardSample Sample(int session, string technology, string rsrp, string extra = "{}") => NetworkDashboardSample.FromFields(session,
            new DateTime(2026, 9, 11, 14, 17, 30), new Dictionary<string, string?>
            { ["network"] = technology, ["rsrp"] = rsrp, ["rsrq"] = "-12", ["sinr"] = "15", ["pci"] = "32", ["mci"] = "3331",
              ["earfcn"] = technology == "4G" ? "315" : "528750", ["band"] = technology == "4G" ? "B1" : "n41", ["mcc"] = "999", ["mnc"] = "200", ["tac"] = "1", ["extra_json"] = extra });
        var nr = Sample(1, "5G", "-80", """{"network_log_fields":{"PS App DL (Mbps)":"0","NR MAC Thpt DL (Mbps)":"25","NR DL Rank":"2","NR MCS":"18","qRxLevMin":"-120dBm"}}""");
        var nr2 = Sample(1, "5G", "-100");
        var invalid = Sample(1, "5G", "2147483647");
        var lte = Sample(2, "4G", "-60");
        var primary = new[] { new L3ReportMessage("e1", 1, "event", "14:17:30", "5G", "", "", "", "ssRsrp", "(new) -75") };
        var report = L3SummaryReportBuilder.Build("test", "Sessions: 1,2", primary, []);
        NetworkLogDashboardFallback.Apply(report, [nr, nr2, invalid, lte]);
        L3DashboardValue K(string name) => report.Kpis.Single(v => v.Parameter == name);
        Check(K("Average SS-RSRP (dBm)").Result == "-75.0" && K("Average SS-RSRP (dBm)").Source == "L3/Event", "L3/Event priority");
        Check(K("Average SS-RSRQ (dB)").Result == "-12.0" && K("Average SS-RSRQ (dB)").Source == "Network Log", "missing value fallback and attribution");
        Check(K("Average LTE RSRP (dBm)").Result == "-60.0", "LTE kept separate from NR");
        Check(K("DL application throughput").Result == "0 Mbps", "real zero measurement is not missing");
        Check(K("NR MCS").Result == "18", "captured extra fields");
        Check(K("NR registration observation ratio").Result == "Not available", "no invented signaling ratio");
        Check(report.Parameters.Single(v => v.Parameter == "q-RxLevMin").Source == "Network Log", "decoded field fallback source");
        Check(report.Messages.Count == 1 && report.NetworkLogRows == 4, "network samples do not become L3/Event messages");
        var onlyNetwork = L3SummaryReportBuilder.Build("network", "Sessions: 1", [], []);
        NetworkLogDashboardFallback.Apply(onlyNetwork, [nr, nr2, invalid]);
        Check(onlyNetwork.Kpis.Single(v => v.Parameter == "Average SS-RSRP (dBm)").Result == "-90.0", "average ignores sentinel");
        Check(onlyNetwork.HasNetworkLogFallback && onlyNetwork.Messages.Count == 0, "network-only dashboard");
        var lteOnly = L3SummaryReportBuilder.Build("LTE", "session2", [], []);
        NetworkLogDashboardFallback.Apply(lteOnly, [lte]);
        Check(lteOnly.Kpis.Single(v => v.Parameter == "Average SS-RSRP (dBm)").Result == "Not available", "LTE cannot fill NR RF");
        Check(NetworkLogDashboardFallback.Matches(lte, new() { Technology = "LTE", TimeFrom = "14:17", TimeTo = "14:18" }), "RAT and time filters");
        Check(!NetworkLogDashboardFallback.Matches(nr, new() { Technology = "LTE" }), "exclude different technology");
        Check(!NetworkLogDashboardFallback.Matches(nr, new() { TimeFrom = "15:00" }), "exclude other time");
        Check(!NetworkLogDashboardFallback.Matches(nr, new() { Search = "paging" }), "message-only filters do not silently use unrelated network samples");
        Check(NetworkLogDashboardFallback.Matches(nr, new() { Sources = "l3" }), "source filter applies to primary messages, fallback still allowed");
        var encoded = NetworkDashboardSample.FromFields(1, null, new Dictionary<string,string?>
            { ["rsrp"] = "-72", ["extra_json"] = """{"rsrp":"-100","network_log_fields":{"NR MCS":"20"}}""" });
        Check(encoded.Get("rsrp") == "-72" && encoded.Get("NR MCS") == "20", "dedicated columns before extra fields");
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        // Exercise the actual import serializer, including existing extras and valid zero measurements.
        var importType = typeof(ProcessCSVController).GetNestedType("NetworkLogModel", BindingFlags.NonPublic)!;
        var importRow = Activator.CreateInstance(importType, nonPublic: true)!;
        importType.GetProperty("Altitude")!.SetValue(importRow, "123");
        var storedExtra = (string)typeof(ProcessCSVController).GetMethod("BuildNetworkLogExtraJson", flags)!.Invoke(null,
            [importRow, new Dictionary<string, string?> { ["NR MCS"] = "18", ["PS App DL (Mbps)"] = "0" }])!;
        var imported = NetworkDashboardSample.FromFields(1, null, new Dictionary<string, string?> { ["extra_json"] = storedExtra });
        Check(imported.Get("NR MCS") == "18" && imported.Get("PS App DL (Mbps)") == "0" && imported.Get("altitude") == "123",
            "import serializer retains dashboard columns and existing extras");
        Check(NetworkLogDashboardFallback.Matches(nr, new() { Technology = " " }), "blank technology keeps all samples");
        var timelineType = typeof(MapViewController).GetNestedType("DiagnosticTimelineRow", BindingFlags.NonPublic)!;
        var payload = typeof(MapViewController).GetMethod("BuildL3SummaryJson", flags)!.Invoke(null, [onlyNetwork, Array.CreateInstance(timelineType, 0)]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var data = json.RootElement.GetProperty("data");
        Check(data.GetProperty("hasData").GetBoolean() && data.GetProperty("totalRows").GetInt32() == 0 && data.GetProperty("networkLogRows").GetInt32() == 3, "JSON fallback metadata");
        Check(data.GetProperty("kpis").EnumerateArray().Single(v => v.GetProperty("parameter").GetString() == "Average SS-RSRP (dBm)").GetProperty("source").GetString() == "Network Log", "JSON source attribution");
        var excel = L3SummaryReportBuilder.WriteExcel(onlyNetwork);
        using (var zip = new ZipArchive(new MemoryStream(excel)))
        using (var stream = zip.GetEntry("xl/worksheets/sheet4.xml")!.Open())
        {
            var xml = XDocument.Load(stream).ToString();
            Check(xml.Contains("-90.0") && xml.Contains("Source: Network Log"), "Excel fallback values and sources");
        }
        var pdf = (byte[])typeof(MapViewController).GetMethod("BuildCombinedL3SummaryPdf", flags)!.Invoke(null, [onlyNetwork, 100000])!;
        Check(Encoding.ASCII.GetString(pdf).Contains("Source: Network Log"), "PDF fallback sources");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "network-fallback.xlsx"), excel);
        File.WriteAllBytes(Path.Combine(output, "network-fallback.pdf"), pdf);
        File.WriteAllText(Path.Combine(output, "network-fallback.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        // Persist actual generated scope SQL for independent execution against an in-memory SQLite fixture.
        var cases = new List<object>();
        foreach (var (ids, upload, expected) in new (int[], int?, int[])[]
        {
            ([1],null,[1]), ([1,2],null,[1,2]), ([],101,[1]), ([],201,[1]), ([],301,[1]),
            ([2],101,[]), ([],203,[]), ([],999,[]), ([],null,[])
        })
        {
            using var cmd = new MySqlCommand();
            var sql = (string)typeof(MapViewController).GetMethod("BuildNetworkDashboardSessionSql", flags)!.Invoke(null, [cmd, ids, upload, true])!;
            cases.Add(new { sql, parameters = cmd.Parameters.Cast<MySqlParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => p.Value), expected });
        }
        File.WriteAllText(Path.Combine(output, "scope-tests.json"), JsonSerializer.Serialize(cases));
        Console.WriteLine("Network fallback regressions passed: priority, missing values, RAT isolation, filters, JSON/Excel/PDF attribution.");
    }
}
