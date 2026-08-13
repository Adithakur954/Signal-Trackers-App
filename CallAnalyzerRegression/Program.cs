using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;
using SignalTracker.Controllers;

static List<DiagnosticAnalyzerInput> ReadRows(string zipPath, string prefix)
{
    using var archive = ZipFile.OpenRead(zipPath);
    var entry = archive.Entries.First(item => Path.GetFileName(item.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && item.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    using var stream = entry.Open();
    using var text = new StreamReader(stream);
    using var csv = new CsvReader(text, new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        BadDataFound = null,
        MissingFieldFound = null,
        HeaderValidated = null
    });
    var rows = new List<DiagnosticAnalyzerInput>();
    foreach (var record in csv.GetRecords<dynamic>())
    {
        var values = ((IDictionary<string, object?>)record).ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value), StringComparer.OrdinalIgnoreCase);
        string? Get(params string[] names) => names.Select(name => values.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        rows.Add(new DiagnosticAnalyzerInput
        {
            Timestamp = Get("timestamp", "time"),
            Category = Get("category", "layer"),
            Name = Get("event", "message", "event_name"),
            Detail = Get("detail", "description", "value"),
            Source = Get("source")
        });
    }
    return rows;
}

static DiagnosticCallRegressionResult Analyze(string zipPath) =>
    MapViewController.AnalyzeDiagnosticRowsForRegression(ReadRows(zipPath, "Event_"), ReadRows(zipPath, "L3_"));

static void Print(string name, DiagnosticCallRegressionResult result)
{
    Console.WriteLine($"{name,-12} {result.Calls.Count,5} {result.Connected,10} {result.NotConnected,14} {result.Dropped,9} {result.Unknown,8}");
    foreach (var call in result.Calls)
        Console.WriteLine($"  {call.Id,-4} {call.Start,-14} {call.End,-14} {call.Result,-15} {call.Reason}");
}

var datasets = args.Length > 0 ? args : new[] { "/home/adi/Downloads/CSTEST.zip", "/home/adi/Downloads/BestL3.zip" };
Console.WriteLine($"{"Dataset",-12} {"Calls",5} {"Connected",10} {"Not Connected",14} {"Dropped",9} {"Unknown",8}");
foreach (var dataset in datasets)
{
    var result = Analyze(dataset);
    Print(Path.GetFileNameWithoutExtension(dataset), result);
    var name = Path.GetFileNameWithoutExtension(dataset);
    var expected = name.Equals("CSTEST", StringComparison.OrdinalIgnoreCase) ? (Calls: 6, Connected: 2, NotConnected: 2, Dropped: 2)
        : name.Equals("BestL3", StringComparison.OrdinalIgnoreCase) ? (Calls: 5, Connected: 5, NotConnected: 0, Dropped: 0)
        : ((int Calls, int Connected, int NotConnected, int Dropped)?)null;
    if (expected.HasValue && (result.Calls.Count, result.Connected, result.NotConnected, result.Dropped) != expected.Value)
        throw new InvalidOperationException($"{name} regression failed. Expected {expected.Value}, actual {(result.Calls.Count, result.Connected, result.NotConnected, result.Dropped)}.");
}

var stale = MapViewController.AnalyzeDiagnosticRowsForRegression(new[]
{
    new DiagnosticAnalyzerInput { Timestamp = "16:03:07.600", Category = "CALL", Name = "CALL_DISCONNECTED", Detail = "07-31 16:02:23.800 EVENT_CALL_END" },
    new DiagnosticAnalyzerInput { Timestamp = "16:03:51.200", Category = "CALL", Name = "CALL_DIAL_INITIATED", Detail = "dial" },
    new DiagnosticAnalyzerInput { Timestamp = "16:03:52.000", Category = "CALL", Name = "CALL_ALERTING", Detail = "ringing" },
    new DiagnosticAnalyzerInput { Timestamp = "16:03:55.000", Category = "CALL", Name = "CALL_DISCONNECTED", Detail = "EVENT_CALL_END" }
}, Array.Empty<DiagnosticAnalyzerInput>());
if (stale.Calls.Count != 1)
    throw new InvalidOperationException($"Stale-disconnect regression failed: expected 1 call, got {stale.Calls.Count}.");

static DiagnosticAnalyzerInput E(string time, string name, string detail) =>
    new() { Timestamp = time, Category = "CALL", Name = name, Detail = detail };
static DiagnosticAnalyzerInput L(string time, string detail) =>
    new() { Timestamp = time, Category = "RRC", Name = detail, Detail = detail };
static void Expect(string name, IEnumerable<DiagnosticAnalyzerInput> events, IEnumerable<DiagnosticAnalyzerInput> l3,
    int total, int connected, int notConnected, int dropped, int unknown = 0)
{
    var result = MapViewController.AnalyzeDiagnosticRowsForRegression(events, l3);
    var actual = (result.Calls.Count, result.Connected, result.NotConnected, result.Dropped, result.Unknown);
    var expected = (total, connected, notConnected, dropped, unknown);
    if (actual != expected)
        throw new InvalidOperationException($"{name} regression failed. Expected {expected}, actual {actual}.");
}

Expect("duplicate CALL_ACTIVE / alerting only", new[]
{
    E("10:00:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:00:01.000", "CALL_ALERTING", "ringing"),
    E("10:00:02.000", "CALL_ACTIVE", "applyLocalCallCapabilities"),
    E("10:00:02.100", "CALL_ACTIVE", "applyLocalCallCapabilities"),
    E("10:00:05.000", "CALL_DISCONNECTED", "no answer")
}, Array.Empty<DiagnosticAnalyzerInput>(), 1, 0, 1, 0);

Expect("SIP 180 without SIP 200", new[]
{
    E("10:01:00.000", "IMS", "SIP INVITE"),
    E("10:01:01.000", "IMS", "SIP 180 Ringing"),
    E("10:01:08.000", "IMS", "SIP CANCEL")
}, Array.Empty<DiagnosticAnalyzerInput>(), 1, 0, 1, 0);

Expect("SIP 200 plus ACK and user hangup", new[]
{
    E("10:02:00.000", "IMS", "SIP INVITE"),
    E("10:02:01.000", "IMS", "SIP 180 Ringing"),
    E("10:02:03.000", "IMS", "SIP/2.0 200 OK"),
    E("10:02:03.100", "IMS", "SIP ACK"),
    E("10:02:30.000", "IMS", "SIP BYE LOCAL_HANGUP")
}, Array.Empty<DiagnosticAnalyzerInput>(), 1, 1, 0, 0);

Expect("connected then abnormal radio loss", new[]
{
    E("10:03:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:03:01.000", "CALL_ALERTING", "ringing"),
    E("10:03:03.000", "CallState", "precise call state ACTIVE connected"),
    E("10:03:20.000", "RADIO", "RADIO LINK FAILURE connection lost"),
    E("10:03:20.500", "CALL_DISCONNECTED", "unexpected network release")
}, Array.Empty<DiagnosticAnalyzerInput>(), 1, 0, 0, 1);

Expect("RRC failure followed by recovery", new[]
{
    E("10:04:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:04:01.000", "CALL_ALERTING", "ringing"),
    E("10:04:03.000", "CallState", "precise call state ACTIVE connected"),
    E("10:04:30.000", "CALL_DISCONNECTED", "LOCAL_HANGUP normal clearing")
}, new[]
{
    L("10:04:15.000", "RRC REESTABLISHMENT FAILURE"),
    L("10:04:16.000", "RRC REESTABLISHMENT COMPLETE")
}, 1, 1, 0, 0);

Expect("failed handover followed by recovery", new[]
{
    E("10:05:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:05:01.000", "CALL_ALERTING", "ringing"),
    E("10:05:03.000", "CallState", "precise call state ACTIVE connected"),
    E("10:05:30.000", "CALL_DISCONNECTED", "LOCAL_HANGUP normal clearing")
}, new[]
{
    L("10:05:15.000", "HANDOVER FAILURE"),
    L("10:05:16.000", "HANDOVER COMPLETE")
}, 1, 1, 0, 0);

Expect("failed handover followed by call loss", new[]
{
    E("10:06:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:06:01.000", "CALL_ALERTING", "ringing"),
    E("10:06:03.000", "CallState", "precise call state ACTIVE connected"),
    E("10:06:16.000", "CALL_DISCONNECTED", "unexpected network release")
}, new[] { L("10:06:15.000", "HANDOVER FAILURE") }, 1, 0, 0, 1);

Expect("two nearby calls", new[]
{
    E("10:07:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:07:01.000", "CALL_ALERTING", "ringing"),
    E("10:07:03.000", "CallState", "connected"),
    E("10:07:10.000", "CALL_DISCONNECTED", "LOCAL_HANGUP"),
    E("10:07:11.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:07:12.000", "CALL_ALERTING", "ringing"),
    E("10:07:18.000", "CALL_DISCONNECTED", "no answer")
}, Array.Empty<DiagnosticAnalyzerInput>(), 2, 1, 1, 0);

Expect("missing Event input", Array.Empty<DiagnosticAnalyzerInput>(), new[] { L("10:08:00.000", "SIP INVITE") }, 0, 0, 0, 0);
Expect("missing L3 input", new[]
{
    E("10:09:00.000", "CALL_DIAL_INITIATED", "dial"),
    E("10:09:03.000", "CallState", "connected"),
    E("10:09:10.000", "CALL_DISCONNECTED", "LOCAL_HANGUP")
}, Array.Empty<DiagnosticAnalyzerInput>(), 1, 1, 0, 0);

Console.WriteLine("Synthetic call-state regressions passed.");
