using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using SignalTracker.Controllers;
using static SignalTracker.Controllers.ExcelReportController;

namespace SignalTracker.Services;

public sealed record L3ReportMessage(string Id, int? SessionId, string Source, string Timestamp,
    string Technology, string Direction, string Channel, string Interface, string Message, string Detail);
public sealed record L3ReportCall(string Call, string Technology, string Start, string End,
    string Result, string SetupTime, string Duration, string Reason);
public sealed record L3DashboardValue(string Parameter, string Result, string Observation = "")
{
    public string Source { get; init; } = "";
}
public sealed record L3ReportTechnology(string Technology, int Rows, string Interfaces);
public sealed record L3ObservedEvents(int EndcSetupRows, int HandoverRows);

public sealed class L3SummaryReport
{
    public string SourceFile { get; init; } = "";
    public string Scope { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<L3ReportMessage> Messages { get; init; } = [];
    public IReadOnlyList<L3ReportCall> Calls { get; init; } = [];
    public IReadOnlyList<L3ReportTechnology> Technologies { get; init; } = [];
    public int NetworkLogRows { get; set; }
    public bool HasNetworkLogFallback => Kpis.Concat(Mobility).Concat(Parameters).Any(v => v.Source == "Network Log");
    public string DashboardSources => HasNetworkLogFallback ? "L3/Event with Network Log fallback" : "L3/Event";
    public List<L3DashboardValue> Kpis { get; } = [];
    public List<L3DashboardValue> Mobility { get; } = [];
    public List<L3DashboardValue> Parameters { get; } = [];
    public L3ObservedEvents ObservedEvents { get; set; } = new(0, 0);
    public Dictionary<string, L3ObservedEvents> ObservedEventsByTechnology { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Builds primary L3/Event statistics. Missing dashboard values may subsequently use Network Log fallback.</summary>
public static class L3SummaryReportBuilder
{
    // Message observations, not deduplicated procedures or success rates.
    private static readonly Regex HandoverText = new(@"\bhand[\s-]?over\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndcText = new(@"\b(?:en[-\s]?dc|scg|secondary\s+cell\s+group|s?gnb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SetupText = new(@"\b(?:setup|addition|add|request|reconfig(?:uration)?|activation|activate|active|establish(?:ment)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static L3ObservedEvents CountObservedEvents(IEnumerable<string> texts)
    {
        var endc = 0;
        var handovers = 0;
        foreach (var text in texts)
        {
            if (EndcText.IsMatch(text) && SetupText.IsMatch(text)) endc++;
            if (HandoverText.IsMatch(text)) handovers++;
        }
        return new(endc, handovers);
    }

    private const string Missing = "Not available";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex Fields = new(@"\b(?<key>[a-zA-Z][a-zA-Z0-9_-]*)\s*[:=]\s*(?<value>\[[^\]\r\n]*\]|[^\s,;|{}]+)", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static string Key(string value) => Regex.Replace(value, @"[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    private static bool Has(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
    private static string Join(IEnumerable<string> values)
    {
        var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToArray();
        return distinct.Length == 0 ? Missing : string.Join(", ", distinct);
    }

    public static L3SummaryReport Build(string source, string scope, IReadOnlyList<L3ReportMessage> messages,
        IReadOnlyList<L3ReportCall> calls)
    {
        var report = new L3SummaryReport
        {
            SourceFile = source, Scope = scope, Messages = messages, Calls = calls,
            Technologies = messages.GroupBy(m => string.IsNullOrWhiteSpace(m.Technology) ? "Unknown" : m.Technology,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => new L3ReportTechnology(g.Key, g.Count(), Join(g.Select(m => m.Interface)))).ToList()
        };
        foreach (var group in messages.GroupBy(m => string.IsNullOrWhiteSpace(m.Technology) ? "Unknown" : m.Technology, StringComparer.OrdinalIgnoreCase))
            report.ObservedEventsByTechnology[group.Key] = CountObservedEvents(group.Select(m => $"{m.Message} {m.Detail} {m.Interface}"));
        report.ObservedEvents = new(report.ObservedEventsByTechnology.Values.Sum(v => v.EndcSetupRows),
            report.ObservedEventsByTechnology.Values.Sum(v => v.HandoverRows));
        report.Mobility.Add(new("Observed EN-DC setup rows", report.ObservedEvents.EndcSetupRows.ToString(Inv), "Message text observations; not correlated setup attempts."));
        report.Mobility.Add(new("Observed handover rows", report.ObservedEvents.HandoverRows.ToString(Inv), "Message text observations; not unique handovers or confirmed successes."));
        // Parse each payload once. Named decoded values remain associated with their source row.
        var decoded = messages.Select(m => (Row: m, Fields: Fields.Matches(m.Detail)
            .GroupBy(x => Key(x.Groups["key"].Value))
            .ToDictionary(g => g.Key, g => g.First().Groups["value"].Value.Trim('"', '\'')))).ToList();
        IEnumerable<string> Values(params string[] names) => decoded.SelectMany(d => names.Select(n =>
                d.Fields.GetValueOrDefault(Key(n))).OfType<string>())
            .Where(v => !Has(v, @"^(?:2147483647|9223372036854775807|unknown|null|n/a|na)$"));
        static bool IsServingCellRow((L3ReportMessage Row, Dictionary<string, string> Fields) decodedRow) =>
            !decodedRow.Fields.ContainsKey("mbands") && !decodedRow.Fields.ContainsKey("mpci")
            || Regex.IsMatch(decodedRow.Row.Detail, @"mRegistered\s*=\s*YES|registered\s*[:=]\s*YES", RegexOptions.IgnoreCase);
        var explicitRats = Values("accessNetworkTechnology", "rat").Where(v => !v.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Equals("NR", StringComparison.OrdinalIgnoreCase) ? "5G NR" : v).ToArray();
        report.Kpis.Add(new("Technology", explicitRats.Length > 0 ? Join(explicitRats) : Join(report.Technologies.Select(t => t.Technology)),
            explicitRats.Length > 0 ? "Explicit registration RAT observations; SA/NSA mode is not inferred from NR alone." : "Message timeline technology labels; these may include contextual inference."));
        var bands = decoded.Where(IsServingCellRow).SelectMany(d =>
        {
            var raw = d.Fields.GetValueOrDefault("mbands") ?? d.Fields.GetValueOrDefault("band");
            if (raw == null) return Array.Empty<string>();
            // LTE CellInfo can contain NR capability/status text. Do not use
            // that text to classify the band; use the row RAT and explicit NR
            // cell identity instead.
            var nr = Has(d.Row.Detail, @"CellIdentityNr")
                || d.Row.Technology.Contains("5G", StringComparison.OrdinalIgnoreCase)
                || d.Row.Technology.Equals("NR", StringComparison.OrdinalIgnoreCase);
            return Identifiers(Number.Matches(raw).Select(n => n.Value)).Where(v => v != "0").Select(n => (nr ? "n" : "B") + n).ToArray();
        });
        report.Kpis.Add(new("Band", Join(bands), "Explicitly logged band values; no ARFCN-to-band guessing."));
        report.Kpis.Add(new("NR ARFCN", Join(Identifiers(Values("mNrArfcn", "NARFCN", "nrArfcn"))), "Decoded L3/Event fields; unavailable identifiers excluded."));
        report.Kpis.Add(new("LTE EARFCN", Join(Identifiers(Values("mEarfcn", "earfcn"))), "Decoded L3/Event fields."));
        var pcis = Identifiers(decoded.Where(IsServingCellRow).SelectMany(d => d.Fields.TryGetValue("mpci", out var pci) ? new[] { pci } : Array.Empty<string>())
            .Concat(messages.SelectMany(m => Regex.Matches(m.Detail,
            @"\bPCell\s+PCI\s*(\d+)", RegexOptions.IgnoreCase)).Select(m => m.Groups[1].Value))).Distinct().ToArray();
        report.Kpis.Add(new("Serving PCI count", pcis.Length == 0 ? Missing : $"{pcis.Length} ({Join(pcis)})", "Cell identity / PCell observations; neighbour PCIs excluded."));
        var ncis = Identifiers(Values("mNci").Concat(messages.SelectMany(m => Regex.Matches(m.Detail, @"\bNCI\s+(\d+)", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value))).Distinct().ToArray();
        report.Kpis.Add(new("NR Cell Identity count", ncis.Length == 0 ? Missing : $"{ncis.Length} ({Join(ncis)})"));
        var plmns = decoded.Where(d => d.Fields.ContainsKey("mmcc") && d.Fields.ContainsKey("mmnc"))
            .Select(d => d.Fields["mmcc"] + "-" + d.Fields["mmnc"])
            .Concat(messages.SelectMany(m => Regex.Matches(m.Detail, @"\bPLMN\s+(\d{3}-\d{2,3})", RegexOptions.IgnoreCase)).Select(m => m.Groups[1].Value));
        report.Kpis.Add(new("PLMN", Join(plmns)));
        report.Kpis.Add(new("TAC", Join(Identifiers(Values("mTac", "tac").Concat(messages.SelectMany(m =>
            Regex.Matches(m.Detail, @"\bTAC\s+(\d+)", RegexOptions.IgnoreCase)).Select(m => m.Groups[1].Value)))
            .Where(v => v != "65535"))));

        foreach (var (name, label, min, max) in new[]
        {
            ("ssRsrp", "SS-RSRP (dBm)", -160d, -20d), ("ssRsrq", "SS-RSRQ (dB)", -50d, 20d),
            ("ssSinr", "SS-SINR (dB)", -50d, 60d), ("rsrp", "LTE RSRP (dBm)", -160d, -20d),
            ("rsrq", "LTE RSRQ (dB)", -50d, 20d)
        })
        {
            var samples = decoded.Where(d => d.Row.Source.Equals("event", StringComparison.OrdinalIgnoreCase))
                .Select(d => Key(d.Row.Message) == Key(name) ? LatestNumber(d.Row.Detail) :
                    d.Fields.TryGetValue(Key(name), out var raw) ? LatestNumber(raw) : null)
                .Where(v => v.HasValue && v >= min && v <= max).Select(v => v!.Value).ToArray();
            var note = samples.Length == 0 ? "No valid Event observations." : $"{samples.Length} Event observations; event-change average, not time weighted.";
            report.Kpis.Add(new($"Average {label}", samples.Length == 0 ? Missing : samples.Average().ToString("0.0", Inv), note));
            report.Kpis.Add(new($"Min / Max {label}", samples.Length == 0 ? Missing : $"{samples.Min().ToString("0.#", Inv)} / {samples.Max().ToString("0.#", Inv)}", note));
        }
        var availability = decoded.Where(d => d.Row.Source.Equals("event", StringComparison.OrdinalIgnoreCase)
            && d.Row.Message.Equals("ENDC_AVAILABILITY", StringComparison.OrdinalIgnoreCase)).ToArray();
        var nrCount = availability.Count(d => d.Fields.GetValueOrDefault("accessnetworktechnology") == "NR");
        report.Kpis.Add(new("NR registration observation ratio", Ratio(nrCount, availability.Length),
            "NR access-technology observations in ENDC_AVAILABILITY; not registration procedure success."));
        foreach (var (label, aliases) in new[]
        {
            ("DL application throughput", new[] { "dlAppThroughput" }), ("UL application throughput", new[] { "ulAppThroughput" }),
            ("DL MAC throughput", new[] { "dlMacThroughput" }), ("UL MAC throughput", new[] { "ulMacThroughput" }),
            ("NR MCS", new[] { "nrMcs" }), ("NR CQI", new[] { "nrCqi" }), ("NR rank", new[] { "nrRank" }),
            ("NR modulation", new[] { "nrModulation" }), ("NR resource blocks", new[] { "nrResourceBlocks" }),
            ("NR slot usage", new[] { "nrSlotUsage" }), ("NR Tx power", new[] { "nrTxPower" })
        })
            report.Kpis.Add(new(label, Join(Values(aliases)), "Explicit decoded field values only; capacity estimates are not throughput."));
        report.Kpis.Add(new("ENDC Setup SR", Missing, "No correlated ENDC setup attempt/outcome calculation is available from this report."));

        var l3 = messages.Where(m => m.Source.Equals("l3", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var group in l3.GroupBy(m => m.Technology))
        {
            var prefix = group.Key + " ";
            var reports = group.Where(m => Has(m.Message, @"meas(?:urement)?\s*report")).ToArray();
            foreach (var evt in new[] { "A1", "A2", "A3", "A4", "A5", "A6", "B1", "B2" })
                report.Mobility.Add(new(prefix + evt + " measurement reports", reports.Count(m =>
                    Has(m.Detail, @"\b(?:Measurement\s+Event|eventId)\s*[:=]\s*" + evt + @"\b")).ToString(Inv)));
            report.Mobility.Add(new(prefix + "Total measurement reports", reports.Length.ToString(Inv)));
            var reconfig = group.Count(m => Has(m.Message, @"reconfig(?:uration)?\b") && !Has(m.Message, "complete"));
            var complete = group.Count(m => Has(m.Message, @"reconfig(?:uration)?\s*complete\b"));
            report.Mobility.Add(new(prefix + "RRC Reconfiguration", reconfig.ToString(Inv)));
            report.Mobility.Add(new(prefix + "RRC Reconfiguration Complete", complete.ToString(Inv)));
            // These are message observations, not correlated request/response pairs.
            // Show a bounded observation ratio only when the counts are mathematically
            // valid; never expose a misleading value above 100%.
            var observationRatio = reconfig > 0 && complete <= reconfig
                ? Ratio(complete, reconfig)
                : Missing;
            var ratioNote = reconfig > 0 && complete <= reconfig
                ? "Bounded message-observation ratio; it is not a correlated procedure success rate."
                : "Not available: setup count is zero or completion observations exceed setup observations.";
            report.Mobility.Add(new(prefix + "Observed completion count ratio", observationRatio, ratioNote));
            report.Mobility.Add(new(prefix + "Paging messages", group.Count(m => Has(m.Message, @"\bpaging\b")).ToString(Inv)));
        }
        var rach = messages.Where(m => m.Source.Equals("event", StringComparison.OrdinalIgnoreCase)
            && Has(m.Message, @"\bRACH\b") && Has(m.Detail, @"\bhandover\b")).ToArray();
        var success = rach.Count(m => Has(m.Message + " " + m.Detail, @"\b(?:OK|success)\b") && !Has(m.Detail, @"\bfail(?:ed|ure)?\b"));
        var failures = rach.Count(m => Has(m.Message + " " + m.Detail, @"\bfail(?:ed|ure)?\b"));
        report.Mobility.Add(new("Handover RACH success events", success.ToString(Inv)));
        report.Mobility.Add(new("Handover RACH failure events", failures.ToString(Inv), "Zero means no failures observed in selected rows."));
        report.Mobility.Add(new("Observed RACH outcome ratio", Ratio(success, success + failures), "Only explicit exported handover RACH outcomes."));
        foreach (var (label, alias) in new[]
        {
            ("q-RxLevMin", "qRxLevMin"), ("q-QualMin", "qQualMin"), ("q-Hyst", "qHyst"),
            ("s-IntraSearchP", "sIntraSearchP"), ("s-NonIntraSearchP", "sNonIntraSearchP"),
            ("threshServingLowP", "threshServingLowP"), ("Cell Reselection Priority", "prio"),
            ("t-ReselectionNR", "tReselNR"), ("Common Subcarrier Spacing", "scsCommon"),
            ("SSB Subcarrier Offset", "ssbOffset"), ("CORESET#0 index", "coreset0"), ("SearchSpace#0 index", "ss0")
        })
            report.Parameters.Add(new(label, Join(Values(alias, label)), "Captured decoded value(s); distinct values retained."));
        // Keep technology context when more than one RAT contributes decoded parameters.
        report.Parameters.Add(new("Parameter source technologies", Join(decoded.Where(d => d.Fields.Keys.Any(k =>
            k is "qrxlevmin" or "qhyst" or "scscommon")).Select(d => d.Row.Technology))));
        foreach (var group in new[] { report.Kpis, report.Mobility, report.Parameters })
            for (var i = 0; i < group.Count; i++)
                group[i] = group[i] with { Source = NetworkLogDashboardFallback.Available(group[i].Result) ? "L3/Event" : "Not available" };

        // Android LTE CellInfo sometimes serializes LTE bands with an "n"
        // prefix. That prefix means NR only when the row is actually NR; for
        // an LTE-only report the canonical value must be B<band>.
        var technologyResult = report.Kpis.FirstOrDefault(v => v.Parameter == "Technology")?.Result ?? "";
        var bandIndex = report.Kpis.FindIndex(v => v.Parameter == "Band");
        if (bandIndex >= 0 && !Has(technologyResult, @"5G|\bNR\b"))
        {
            var band = report.Kpis[bandIndex];
            var normalizedBand = Regex.Replace(band.Result, @"\bn(\d+)\b", "B$1", RegexOptions.IgnoreCase);
            report.Kpis[bandIndex] = band with { Result = normalizedBand };
        }
        return report;
    }

    private static double? LatestNumber(string text)
    {
        // Never fall back to the old value when the new value is an unavailable sentinel.
        var latest = Regex.Split(text, @"->|→").Last();
        var match = Number.Match(latest);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, Inv, out var value) ? value : null;
    }
    private static IEnumerable<string> Identifiers(IEnumerable<string> values) => values
        .Select(v => Regex.Match(v, @"^-?\d+").Value)
        .Where(v => long.TryParse(v, NumberStyles.Integer, Inv, out var n) && n >= 0 && n != int.MaxValue && n != long.MaxValue)
        .Distinct();
    private static string Ratio(int numerator, int denominator) => denominator == 0 ? Missing :
        $"{(100d * numerator / denominator).ToString("0.0", Inv)}% ({numerator}/{denominator})";

    public static byte[] WriteExcel(L3SummaryReport report)
    {
        var book = new XlsxWorkbook();
        XlsxSheet Sheet(string name, double[] widths)
        {
            var sheet = new XlsxSheet(name) { ColumnWidths = widths };
            book.Sheets.Add(sheet);
            sheet.Rows.Add(XlsxRow.Title(name, widths.Length));
            return sheet;
        }
        var summary = Sheet("Summary", [28, 110]);
        summary.Rows.Add(XlsxRow.Data("Call and technology summary, L3 Dashboard and filtered sheet messages."));
        summary.Rows.Add(XlsxRow.Blank());
        summary.Rows.Add(Data("Source File", report.SourceFile));
        summary.Rows.Add(Data("Report Scope", report.Scope));
        summary.Rows.Add(XlsxRow.FromText("Generated At", report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", Inv)));
        summary.Rows.Add(XlsxRow.FromText("Exported Rows", report.Messages.Count.ToString(Inv)));
        summary.Rows.Add(XlsxRow.FromText("Technologies", Join(report.Technologies.Select(t => t.Technology))));
        summary.Rows.Add(XlsxRow.FromText("Sources", report.DashboardSources));
        summary.Rows.Add(XlsxRow.FromText("Network Log fallback samples", report.NetworkLogRows.ToString(Inv)));
        summary.Rows.Add(XlsxRow.FromText("Call Scope", "Calls associated with filtered rows; outcomes use the complete loaded session window."));
        var calls = Sheet("Call Summary", [16, 18, 20, 20, 22, 20, 20, 90]);
        calls.Rows.Add(XlsxRow.Blank());
        calls.Rows.Add(XlsxRow.Header("Call", "Technology", "Start", "End", "Result", "Setup time (s)", "Duration (s)", "Reason"));
        if (report.Calls.Count == 0) calls.Rows.Add(XlsxRow.Data("No call session is associated with the filtered signaling rows."));
        foreach (var c in report.Calls) calls.Rows.Add(Data(c.Call, c.Technology, c.Start, c.End, c.Result, c.SetupTime, c.Duration, c.Reason));
        var tech = Sheet("Technology Summary", [24, 16, 100]);
        tech.Rows.Add(XlsxRow.Blank());
        tech.Rows.Add(XlsxRow.Header("Technology", "Rows", "Interfaces"));
        foreach (var t in report.Technologies) tech.Rows.Add(Data(t.Technology, t.Rows.ToString(Inv), t.Interfaces));
        var dashboard = Sheet("L3 Dashboard", [36, 48, 75, 4, 46, 50, 4, 32, 48]);
        dashboard.Rows.Add(XlsxRow.Data("L3/Event values take priority; missing values use matching Network Log samples where available. Sources are labelled."));
        dashboard.Rows.Add(XlsxRow.Blank());
        dashboard.Rows.Add(XlsxRow.Header("KPI / Parameter", "Result from Log", "Observation", "", "Mobility KPI", "Result", "", "Decoded Parameter", "Decoded Value"));
        for (var i = 0; i < new[] { report.Kpis.Count, report.Mobility.Count, report.Parameters.Count }.Max(); i++)
        {
            var k = report.Kpis.ElementAtOrDefault(i); var m = report.Mobility.ElementAtOrDefault(i); var p = report.Parameters.ElementAtOrDefault(i);
            dashboard.Rows.Add(Data(k?.Parameter, k?.Result, k == null ? "" : $"Source: {k.Source}. {k.Observation}", "", m?.Parameter,
                m == null ? "" : m.Result + (m.Observation.Length > 0 ? " — " + m.Observation : ""), "", p?.Parameter, p == null ? "" : p.Result + " [Source: " + p.Source + "]"));
        }
        var messages = Sheet("Sheet Messages", [24, 16, 24, 24, 48, 120]);
        messages.Rows.Add(XlsxRow.Data("Captured or decoded detail. Long details continue on additional rows with repeated message columns."));
        messages.Rows.Add(XlsxRow.Blank());
        messages.Rows.Add(XlsxRow.Header("Timestamp", "Direction", "Channel", "Interface", "Message", "Detail"));
        foreach (var m in report.Messages)
        {
            var detail = Clean(m.Detail);
            // XLSX cells have a 32,767 UTF-16 character limit. Preserve the entire payload.
            do
            {
                var length = Math.Min(detail.Length, 32000);
                if (length > 0 && char.IsHighSurrogate(detail[length - 1])) length--;
                messages.Rows.Add(Data(m.Timestamp, m.Direction, m.Channel, m.Interface, m.Message, detail[..length]));
                detail = detail[length..];
            } while (detail.Length > 0);
        }
        return SimpleXlsxWriter.Write(book);
    }

    private static XlsxRow Data(params string?[] values)
    {
        var row = new XlsxRow();
        row.Cells.AddRange(values.Select(v => XlsxCell.Text(Clean(v), 7)));
        return row;
    }
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i])) result.Append(value[i]);
            else if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            { result.Append(value[i]); result.Append(value[++i]); }
        }
        return result.ToString();
    }
}
