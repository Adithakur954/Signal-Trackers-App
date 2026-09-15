using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class L3SummaryReportRegression
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("L3 summary regression: " + name);
    }

    internal static void Run(string? zipPath, string? outputDirectory)
    {
        var rows = new List<L3ReportMessage>
        {
            Row("event", "ssRsrp", "-100 -> -80"), Row("event", "ssRsrp", "-80 -> 2147483647"),
            Row("event", "ssRsrp", "(new) -90"), Row("l3", "NR SIB3", "intraFreqNeigh: PCI78(dB0) PCI32(dB0)"),
            Row("l3", "NR RRC Reconfiguration", "measCfg: A1 A2 A3"),
            Row("l3", "NR Measurement Report", "Measurement Event: A2 | PCell PCI32 -90dBm"),
            Row("l3", "NR RRC Reconfiguration Complete", "xact=1"),
            Row("event", "ENDC_AVAILABILITY", "accessNetworkTechnology=NR CellIdentityNr mPci=32 mBands=[41] mNci=3331"),
            Row("event", "ENDC_AVAILABILITY", "accessNetworkTechnology=UNKNOWN"),
            Row("event", "LINK_CAPACITY_EST", "downlinkCapacityKbps=200000"),
            Row("l3", "NR SIB2", "qRxLevMin=-120dBm qQualMin=-43 qHyst=dB3 prio=7"),
            Row("event", "Cell identity", "mNrArfcn=-1 mTac=-1"),
            Row("l3", "NR Measurement Report", "NARFCN=528750(measId) TAC 1")
        };
        var report = L3SummaryReportBuilder.Build("test", "all", rows, []);
        string K(string name) => report.Kpis.Single(k => k.Parameter == name).Result;
        Check(K("Average SS-RSRP (dBm)") == "-85.0", "exclude new sentinel; do not count the previous value");
        Check(K("Serving PCI count") == "1 (32)", "exclude neighbour cells");
        Check(K("Band") == "n41", "explicit NR band");
        Check(K("NR ARFCN") == "528750" && K("TAC") == "1", "exclude negative identifiers and normalize annotated ARFCN");
        Check(K("NR registration observation ratio") == "50.0% (1/2)", "observation ratio");
        Check(K("DL application throughput") == "Not available", "capacity is not throughput");
        Check(report.Mobility.Single(k => k.Parameter == "5G A2 measurement reports").Result == "1", "count transmitted report only");
        Check(report.Mobility.Single(k => k.Parameter == "5G A3 measurement reports").Result == "0", "configuration is not a report");
        Check(report.Parameters.Single(k => k.Parameter == "q-RxLevMin").Result == "-120dBm", "decode parameter");
        Check(report.Technologies.Sum(t => t.Rows) == rows.Count, "technology counts cover every row");
        var empty = L3SummaryReportBuilder.Build("empty", "all", [], []);
        Check(empty.Kpis.Single(k => k.Parameter == "Average SS-RSRP (dBm)").Result == "Not available", "empty measurements");
        CheckJson(empty);
        CheckJson(L3SummaryReportBuilder.Build("multiple", "Sessions: 1,2", rows.Concat(rows.Select(r => r with { SessionId = 2 })).ToList(),
            [new L3ReportCall("Call-1", "5G", "12:00:00", "12:00:30", "Connected", "2", "28", "Normal release")]));

        var filter = new L3SummaryFilters { Sources = "l3", Channel = "UL-DCCH", Technology = "5G", TimeFrom = "23:00", TimeTo = "01:00" };
        Check(filter.Validate() == null && filter.Matches(rows[5] with { Timestamp = "00:30:00", Channel = "UL-DCCH" }, false), "overnight time and combined filters");
        Check(!filter.Matches(rows[5] with { Timestamp = "12:00:00", Channel = "UL-DCCH" }, false), "time exclusion");
        Check(new L3SummaryFilters { TimeFrom = "bad" }.Validate() != null, "invalid time rejected");
        Check(new L3SummaryFilters { Sources = "network" }.Validate() != null, "only L3/Event sources");
        var filtered = rows.Where(r => new L3SummaryFilters { Sources = "event", Search = "ssRsrp" }.Matches(r, false)).ToList();
        Check(L3SummaryReportBuilder.Build("filtered", "event", filtered, []).Messages.Count == 3, "filters precede aggregation");

        var longDetail = "=SUM(A1:A2)\u0001" + new string('x', 40000) + "📡";
        var escaped = L3SummaryReportBuilder.Build("escaping", "all", [Row("l3", "<message>&", longDetail)], []);
        using (var archive = new ZipArchive(new MemoryStream(L3SummaryReportBuilder.WriteExcel(escaped))))
        {
            var doc = ReadXml(archive, "xl/worksheets/sheet5.xml");
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Check(!doc.Descendants(ns + "f").Any(), "payload is text, never a formula");
            var details = doc.Descendants(ns + "c").Where(c => c.Attribute("r")!.Value.StartsWith("F")
                && int.Parse(c.Attribute("r")!.Value[1..]) >= 5).Select(c => c.Descendants(ns + "t").Single().Value);
            Check(string.Concat(details) == longDetail.Replace("\u0001", ""), "long XML-safe Unicode payload survives continuation rows");
        }

        if (zipPath != null)
        {
            var messages = ReadImportedTimeline(zipPath);
            report = L3SummaryReportBuilder.Build(Path.GetFileNameWithoutExtension(zipPath), "Local ZIP L3/Event CSV verification", messages, []);
            if (Path.GetFileName(zipPath).Contains("sunil1", StringComparison.OrdinalIgnoreCase))
            {
                Check(messages.Count == 1773, "Sunil imported scope: 198 L3 + 1575 Event rows");
                Check(report.Kpis.Single(k => k.Parameter == "Average SS-RSRP (dBm)").Result == "-83.2", "Sunil RSRP average");
                Check(report.Kpis.Single(k => k.Parameter == "Average SS-RSRQ (dB)").Result == "-12.5", "Sunil RSRQ average");
                Check(report.Kpis.Single(k => k.Parameter == "Average SS-SINR (dB)").Result == "11.3", "Sunil SINR average");
                Check(report.Mobility.Single(k => k.Parameter == "5G Total measurement reports").Result == "76", "Sunil measurement reports");
                Check(report.Mobility.Single(k => k.Parameter == "5G RRC Reconfiguration").Result == "19", "Sunil reconfigurations");
                Check(report.Mobility.Single(k => k.Parameter == "5G RRC Reconfiguration Complete").Result == "19", "Sunil completions");
                Check(report.Mobility.Single(k => k.Parameter == "Handover RACH success events").Result == "8", "Sunil RACH outcomes");
            }
        }
        var xlsx = L3SummaryReportBuilder.WriteExcel(report);
        var summaryJson = CheckJson(report);
        using (var archive = new ZipArchive(new MemoryStream(xlsx)))
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Check(ReadXml(archive, "xl/workbook.xml").Descendants(ns + "sheet").Select(s => s.Attribute("name")!.Value)
                .SequenceEqual(new[] { "Summary", "Call Summary", "Technology Summary", "L3 Dashboard", "Sheet Messages" }), "five-sheet workbook layout");
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml")))
            { using var stream = entry.Open(); XDocument.Load(stream); }
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var reader = ExcelReaderFactory.CreateReader(new MemoryStream(xlsx)))
        {
            Check(reader.ResultsCount == 5, "workbook opens in the independent Excel reader");
            var sheetNames = new List<string>();
            do { sheetNames.Add(reader.Name); while (reader.Read()) { } } while (reader.NextResult());
            Check(sheetNames.Contains("L3 Dashboard"), "dashboard is readable as a worksheet");
        }
        var pdf = (byte[])typeof(MapViewController).GetMethod("BuildCombinedL3SummaryPdf", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [report, 100000])!;
        var pdfText = Encoding.ASCII.GetString(pdf);
        Check(pdfText.StartsWith("%PDF-") && pdfText.Contains("L3 Dashboard") && pdfText.Contains("Sheet Messages"), "combined PDF sections");
        Check(pdfText.Contains("Average SS-RSRP") && pdfText.Contains(report.Kpis.Single(k => k.Parameter == "Average SS-RSRP (dBm)").Result), "PDF uses shared dashboard values");
        if (outputDirectory != null)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, "l3-combined-summary.xlsx"), xlsx);
            File.WriteAllBytes(Path.Combine(outputDirectory, "l3-combined-summary.pdf"), pdf);
            File.WriteAllText(Path.Combine(outputDirectory, "l3-summary.json"), summaryJson);
        }
        Console.WriteLine($"L3 summary regressions passed; {report.Messages.Count} messages; XLSX {xlsx.Length:N0} bytes; PDF {pdf.Length:N0} bytes.");
    }

    private static string CheckJson(L3SummaryReport report)
    {
        var payload = typeof(MapViewController).GetMethod("BuildL3SummaryJson", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [report, Array.CreateInstance(typeof(MapViewController).GetNestedType("DiagnosticTimelineRow", BindingFlags.NonPublic)!, 0)]);
        // Mirror Program.cs: property naming policy is null, so field names must be explicit.
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Check(root.GetProperty("status").GetInt32() == 1, "JSON status");
        var data = root.GetProperty("data");
        Check(data.GetProperty("scope").GetString() == report.Scope, "JSON records the selected scope");
        Check(data.GetProperty("hasData").GetBoolean() == (report.Messages.Count > 0), "JSON empty-state indicator");
        Check(data.GetProperty("totalRows").GetInt32() == report.Messages.Count, "JSON total message count");
        Check(data.GetProperty("l3Rows").GetInt32() + data.GetProperty("eventRows").GetInt32() == report.Messages.Count, "JSON source counts");
        foreach (var (key, values) in new[] { ("kpis", report.Kpis), ("mobility", report.Mobility), ("parameters", report.Parameters) })
        {
            var actual = data.GetProperty(key).EnumerateArray().Select(v => new L3DashboardValue(
                v.GetProperty("parameter").GetString()!, v.GetProperty("result").GetString()!, v.GetProperty("observation").GetString()!) { Source = v.GetProperty("source").GetString()! });
            Check(actual.SequenceEqual(values), "JSON/Excel/PDF calculation parity: " + key);
        }
        Check(data.GetProperty("technologies").EnumerateArray().Sum(t => t.GetProperty("rows").GetInt32()) == report.Messages.Count, "JSON technology counts");
        Check(data.GetProperty("calls").GetArrayLength() == report.Calls.Count, "JSON call summaries");
        if (report.Calls.Count > 0)
            Check(data.GetProperty("calls")[0].GetProperty("setupTime").GetString() == report.Calls[0].SetupTime, "JSON call timing");
        Check(!data.TryGetProperty("messages", out _), "JSON summary avoids the full message payload");
        return json;
    }

    private static L3ReportMessage Row(string source, string message, string detail) =>
        new(Guid.NewGuid().ToString(), 1, source, "12:00:00", "5G", "", "", "NR-RRC", message, detail);
    private static XDocument ReadXml(ZipArchive archive, string name)
    { using var stream = archive.GetEntry(name)!.Open(); return XDocument.Load(stream); }

    // Exercise the same timeline conversion and column/detail fallback used by the HTTP exports.
    private static List<L3ReportMessage> ReadImportedTimeline(string path)
    {
        var controller = typeof(MapViewController);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        IList List(string name) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(controller.GetNestedType(name, BindingFlags.NonPublic)!))!;
        var events = List("DiagnosticEventRow"); var l3 = List("DiagnosticL3Row"); var calls = List("DiagnosticCallRow");
        using var archive = ZipFile.OpenRead(path);
        foreach (var isL3 in new[] { true, false })
        {
            var prefix = isL3 ? "L3_" : "Event_";
            var entry = archive.Entries.First(e => e.Name.StartsWith(prefix) && e.Name.EndsWith(".csv"));
            using var stream = entry.Open(); using var text = new StreamReader(stream);
            using var csv = new CsvReader(text, new CsvConfiguration(CultureInfo.InvariantCulture) { BadDataFound = null });
            var number = 0;
            foreach (var record in csv.GetRecords<dynamic>())
            {
                var values = ((IDictionary<string, object?>)record).ToDictionary(p => p.Key, p => p.Value);
                if (!isL3 && Convert.ToString(values.GetValueOrDefault("event")) == "CallState" && Convert.ToString(values.GetValueOrDefault("detail")) == "Idle (ended)") continue;
                var imported = DiagnosticFieldRegression.Build(isL3, values);
                var type = controller.GetNestedType(isL3 ? "DiagnosticL3Row" : "DiagnosticEventRow", BindingFlags.NonPublic)!;
                var row = Activator.CreateInstance(type)!;
                foreach (var property in type.GetProperties().Where(p => p.CanWrite))
                {
                    var source = imported.GetType().GetProperty(property.Name);
                    if (source != null) property.SetValue(row, source.GetValue(imported));
                }
                type.GetProperty("Id")!.SetValue(row, (long)++number);
                (isL3 ? l3 : events).Add(row);
            }
        }
        var timeline = (IEnumerable)controller.GetMethod("BuildDiagnosticTimelineRows", flags)!.Invoke(null, [events, l3, calls])!;
        return timeline.Cast<object>().Select(row => (L3ReportMessage)controller.GetMethod("ToL3ReportMessage", flags)!.Invoke(null, [row])!).ToList();
    }
}
