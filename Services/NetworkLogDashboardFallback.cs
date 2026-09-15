using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class NetworkDashboardSample
{
    public int SessionId { get; init; }
    public DateTime? Timestamp { get; init; }
    public Dictionary<string, string?> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(params string[] names)
    {
        foreach (var name in names)
            if (Fields.TryGetValue(NetworkLogDashboardFallback.Normalize(name), out var value)
                && NetworkLogDashboardFallback.Available(value)) return value!.Trim();
        return null;
    }
    public string Technology => NetworkLogDashboardFallback.Technology(Get("network", "Network Type") ?? "");

    public static NetworkDashboardSample FromFields(int sessionId, DateTime? timestamp, IDictionary<string, string?> fields)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in fields) values[NetworkLogDashboardFallback.Normalize(pair.Key)] = pair.Value;
        if (fields.TryGetValue("extra_json", out var extra) && !string.IsNullOrWhiteSpace(extra))
        {
            try
            {
                using var doc = JsonDocument.Parse(extra);
                void Add(JsonElement node)
                {
                    if (node.ValueKind != JsonValueKind.Object) return;
                    foreach (var p in node.EnumerateObject())
                    {
                        if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        {
                            var key = NetworkLogDashboardFallback.Normalize(p.Name);
                            if (!values.TryGetValue(key, out var old) || !NetworkLogDashboardFallback.Available(old))
                                values[key] = p.Value.ToString();
                        }
                    }
                }
                Add(doc.RootElement);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("network_log_fields", out var captured)) Add(captured);
            }
            catch (JsonException) { /* Older uploads may contain non-JSON extra text. */ }
        }
        return new() { SessionId = sessionId, Timestamp = timestamp, Fields = values };
    }
}

/// <summary>Fills missing dashboard values only; never adds Network Log samples to the signaling timeline.</summary>
public static class NetworkLogDashboardFallback
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string Normalize(string value) => Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    public static bool Available(string? value) => !string.IsNullOrWhiteSpace(value)
        && !Regex.IsMatch(value.Trim(), @"^(?:not available|unknown|null|n/?a|--?|\u2014|2147483647|9223372036854775807)$", RegexOptions.IgnoreCase);
    public static string Technology(string value)
    {
        if (Regex.IsMatch(value, @"5G|\bNR\b", RegexOptions.IgnoreCase)) return "5G";
        if (Regex.IsMatch(value, @"4G|LTE", RegexOptions.IgnoreCase)) return "LTE";
        if (Regex.IsMatch(value, @"3G|WCDMA|UMTS|HSPA", RegexOptions.IgnoreCase)) return "3G";
        if (Regex.IsMatch(value, @"2G|GSM|GPRS|EDGE", RegexOptions.IgnoreCase)) return "2G";
        return "Unknown";
    }

    // Extra CSV headers retained at import for metrics not represented by dedicated DB columns.
    public static readonly string[] CapturedHeaders =
    [
        "PS App DL (Mbps)", "PS App UL (Mbps)", "NR MAC Thpt DL (Mbps)", "NR MAC Thpt UL (Mbps)",
        "LTE MAC Thpt DL (Mbps)", "LTE MAC Thpt UL (Mbps)", "NR MCS", "DL MCS", "NR CQI", "NR DL Rank",
        "NR DL Modulation", "NR DL RB", "NR DL Slot Usage (%)", "PUSCH Tx (dBm)",
        "qRxLevMin", "qQualMin", "qHyst", "sIntraSearchP", "sNonIntraSearchP", "threshServingLowP",
        "cellReselectionPriority", "tReselNR", "scsCommon", "ssbOffset", "coreset0", "ss0"
    ];

    public static bool Matches(NetworkDashboardSample row, L3SummaryFilters filter)
    {
        // Message-only filters have no equivalent in sampled Network Log rows.
        if (filter.FailuresOnly || !string.IsNullOrWhiteSpace(filter.Search)
            || (!string.IsNullOrWhiteSpace(filter.Interface) && !filter.Interface.Equals("all", StringComparison.OrdinalIgnoreCase))) return false;
        var sampleFilter = new L3SummaryFilters
        {
            Technology = string.IsNullOrWhiteSpace(filter.Technology) ? null : string.Join(",", filter.Technology.Split(',').Select(t => t.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? "all" : Technology(t))),
            Direction = filter.Direction, Channel = filter.Channel, TimeFrom = filter.TimeFrom, TimeTo = filter.TimeTo
        };
        return sampleFilter.Matches(new("", row.SessionId, "network", row.Timestamp?.ToString("HH:mm:ss.fff", Inv) ?? "",
            row.Technology, row.Get("direction") ?? "", row.Get("channel") ?? "", "", "", ""), false);
    }

    public static void Apply(L3SummaryReport report, IReadOnlyList<NetworkDashboardSample> samples)
    {
        // Neighbour rows are useful for a separate neighbour view, but must
        // never affect serving-cell dashboard KPIs.
        samples = samples.Where(IsServingSample).ToList();
        report.NetworkLogRows = samples.Count;
        if (samples.Count == 0) return;
        void Fill(List<L3DashboardValue> group, string name, string? value, string note, string source = "Network Log")
        {
            var index = group.FindIndex(v => v.Parameter == name);
            if (index < 0 || Available(group[index].Result) || !Available(value)) return;
            group[index] = new(name, value!, $"{source}: {note}") { Source = source };
        }
        string? Join(IEnumerable<string?> values)
        {
            var unique = values.Where(Available).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToArray();
            return unique.Length == 0 ? null : string.Join(", ", unique);
        }
        IEnumerable<string?> Values(params string[] names) => samples.Select(r => r.Get(names));
        void Ids(string name, IEnumerable<string?> values, bool count = false)
        {
            var ids = values.Where(v => long.TryParse(v, NumberStyles.Integer, Inv, out var n) && n >= 0 && n != int.MaxValue && n != long.MaxValue)
                .Select(v => v!).Distinct().OrderBy(v => v).ToArray();
            Fill(report.Kpis, name, ids.Length == 0 ? null : (count ? $"{ids.Length} ({string.Join(", ", ids)})" : string.Join(", ", ids)), "captured serving-cell identifiers; unavailable values excluded.");
        }
        Fill(report.Kpis, "Technology", Join(samples.Select(r => r.Technology)), "captured Network Type; SA/NSA is not inferred.");
        Fill(report.Kpis, "Band", Join(Values("band").Where(v => v != "-1")), "captured band values.");
        Ids("Serving PCI count", Values("pci"), true);
        Ids("NR Cell Identity count", samples.Where(r => r.Technology == "5G").Select(r => r.Get("mci", "nci", "cell_id")), true);
        Ids("NR ARFCN", samples.Where(r => r.Technology == "5G").Select(r => r.Get("earfcn", "nrArfcn")));
        Ids("LTE EARFCN", samples.Where(r => r.Technology == "LTE").Select(r => r.Get("earfcn")));
        Ids("TAC", Values("tac").Where(v => !string.Equals(v?.Trim(), "65535", StringComparison.OrdinalIgnoreCase)));
        Fill(report.Kpis, "PLMN", Join(samples.Select(r =>
        {
            var mcc = r.Get("mcc", "m_mcc"); var mnc = r.Get("mnc", "m_mnc");
            return int.TryParse(mcc, out var country) && country is >= 100 and <= 999
                && int.TryParse(mnc, out var network) && network is >= 0 and <= 999 ? mcc + "-" + mnc!.PadLeft(2, '0') : null;
        })), "captured MCC/MNC pairs.");
        foreach (var (label, technology, field, min, max) in new[]
        {
            ("SS-RSRP (dBm)", "5G", "rsrp", -160d, -20d), ("SS-RSRQ (dB)", "5G", "rsrq", -50d, 20d),
            ("SS-SINR (dB)", "5G", "sinr", -50d, 60d), ("LTE RSRP (dBm)", "LTE", "rsrp", -160d, -20d),
            ("LTE RSRQ (dB)", "LTE", "rsrq", -50d, 20d)
        })
        {
            var numbers = samples.Where(r => r.Technology == technology).Select(r => Numeric(r.Get(field), min, max)).OfType<double>().ToArray();
            if (numbers.Length == 0) continue;
            var note = $"{numbers.Length} valid {technology} Network Log samples; sample-weighted, not time-weighted or Event-change statistics.";
            Fill(report.Kpis, "Average " + label, numbers.Average().ToString("0.0", Inv), note);
            Fill(report.Kpis, "Min / Max " + label, $"{numbers.Min().ToString("0.#", Inv)} / {numbers.Max().ToString("0.#", Inv)}", note);
        }

        static bool IsServingSample(NetworkDashboardSample sample)
        {
            var primary = sample.Get("primary");
            if (Available(primary))
                return primary.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || primary.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || primary.Equals("1", StringComparison.OrdinalIgnoreCase);

            // Older rows may not have the dedicated primary column.
            return !Regex.IsMatch(sample.Get("network") ?? "", @"\bneighbou?r\b", RegexOptions.IgnoreCase);
        }

        // Derive only radio values with a physically meaningful formula and
        // only when every required measurement is present in the same sample.
        // Never estimate a value from a loosely related field or a default.
        void FillDerivedRadioMetric(string technology, string metric, double min, double max,
            Func<NetworkDashboardSample, double?> calculate, string formula)
        {
            var values = samples
                .Where(sample => sample.Technology == technology)
                .Select(calculate)
                .OfType<double>()
                .Where(value => value >= min && value <= max)
                .ToArray();
            if (values.Length == 0) return;

            Fill(report.Kpis, "Average " + metric, values.Average().ToString("0.0", Inv),
                $"{values.Length} valid samples; {formula}.", "RF calculated");
            Fill(report.Kpis, "Min / Max " + metric,
                $"{values.Min().ToString("0.#", Inv)} / {values.Max().ToString("0.#", Inv)}",
                $"{values.Length} valid samples; {formula}.", "RF calculated");
        }

        static double? RsrqFromRsrpRssi(NetworkDashboardSample sample)
        {
            var rsrp = Numeric(sample.Get("rsrp", "ssRsrp"), -160, -20);
            var rssi = Numeric(sample.Get("rssi"), -200, 50);
            var resourceBlocks = Numeric(sample.Get(
                "NR DL RB", "LTE DL RB", "DL RB", "resource_blocks", "resource blocks", "rb"), 1, 10000);
            if (!rsrp.HasValue || !rssi.HasValue || !resourceBlocks.HasValue) return null;
            return 10d * Math.Log10(resourceBlocks.Value) + rsrp.Value - rssi.Value;
        }

        static double? RsrpFromRsrqRssi(NetworkDashboardSample sample)
        {
            var rsrq = Numeric(sample.Get("rsrq", "ssRsrq"), -50, 20);
            var rssi = Numeric(sample.Get("rssi"), -200, 50);
            var resourceBlocks = Numeric(sample.Get(
                "NR DL RB", "LTE DL RB", "DL RB", "resource_blocks", "resource blocks", "rb"), 1, 10000);
            if (!rsrq.HasValue || !rssi.HasValue || !resourceBlocks.HasValue) return null;
            return rsrq.Value + rssi.Value - 10d * Math.Log10(resourceBlocks.Value);
        }

        static double? SinrFromNoise(NetworkDashboardSample sample)
        {
            var rsrp = Numeric(sample.Get("rsrp", "ssRsrp"), -160, -20);
            var noise = Numeric(sample.Get("noise", "noise_floor", "noise floor", "interference"), -200, 50);
            return rsrp.HasValue && noise.HasValue ? rsrp.Value - noise.Value : null;
        }

        FillDerivedRadioMetric("5G", "SS-RSRQ (dB)", -50, 20,
            RsrqFromRsrpRssi, "RSRQ = 10*log10(resource blocks) + RSRP - RSSI");
        FillDerivedRadioMetric("LTE", "LTE RSRQ (dB)", -50, 20,
            RsrqFromRsrpRssi, "RSRQ = 10*log10(resource blocks) + RSRP - RSSI");
        FillDerivedRadioMetric("5G", "SS-RSRP (dBm)", -160, -20,
            RsrpFromRsrqRssi, "RSRP = RSRQ + RSSI - 10*log10(resource blocks)");
        FillDerivedRadioMetric("LTE", "LTE RSRP (dBm)", -160, -20,
            RsrpFromRsrqRssi, "RSRP = RSRQ + RSSI - 10*log10(resource blocks)");
        FillDerivedRadioMetric("5G", "SS-SINR (dB)", -50, 60,
            SinrFromNoise, "SINR = RSRP - measured noise/interference");

        void Metric(string label, string[] aliases, double min, double max, string unit, bool nrOnly)
        {
            var numbers = samples.Where(r => !nrOnly || r.Technology == "5G")
                .Select(r => Numeric(r.Get(aliases), min, max)).OfType<double>().ToArray();
            if (numbers.Length == 0) return;
            Fill(report.Kpis, label, numbers.Average().ToString("0.###", Inv) + unit,
                $"mean of {numbers.Length} valid captured samples ({string.Join(" / ", aliases)}); no capacity estimates.");
        }
        Metric("DL application throughput", ["PS App DL (Mbps)", "DL THPT", "dl_tpt", "DL Delivered (Mbps)", "PDCP DL Thpt (Mbps)"], 0, 1000000, " Mbps", false);
        Metric("UL application throughput", ["PS App UL (Mbps)", "UL THPT", "ul_tpt", "UL Delivered (Mbps)"], 0, 1000000, " Mbps", false);
        // Keep MAC statistics grouped by RAT when an upload contains both LTE and NR.
        foreach (var direction in new[] { "DL", "UL" })
        {
            var parts = new List<string>();
            foreach (var rat in new[] { ("LTE", "LTE"), ("5G", "NR") })
            {
                var aliases = rat.Item2 == "LTE"
                    ? new[] { $"LTE MAC Thpt {direction} (Mbps)", $"LTE MAC Thpt {direction} delivered (Mbps)" }
                    : new[] { $"NR MAC Thpt {direction} (Mbps)", $"NR MAC Thpt {direction} delivered (Mbps)" };
                var vals = samples.Where(r => r.Technology == rat.Item1)
                    .Select(r => Numeric(r.Get(aliases), 0, 1000000)).OfType<double>().ToArray();
                if (vals.Length > 0) parts.Add($"{rat.Item2}: {vals.Average().ToString("0.###", Inv)} Mbps ({vals.Length} samples)");
            }
            Fill(report.Kpis, direction + " MAC throughput", parts.Count == 0 ? null : string.Join("; ", parts), "captured MAC throughput means, grouped by RAT.");
        }
        Metric("NR CQI", ["NR CQI", "cqi"], 0, 15, "", true);
        Metric("NR MCS", ["NR MCS", "DL MCS"], 0, 31, "", true);
        Metric("NR resource blocks", ["NR DL RB"], 0, 10000, "", true);
        Metric("NR slot usage", ["NR DL Slot Usage (%)"], 0, 100, "%", true);
        Metric("NR Tx power", ["PUSCH Tx (dBm)"], -100, 100, " dBm", true);
        Fill(report.Kpis, "NR rank", Join(samples.Where(r => r.Technology == "5G").Select(r => r.Get("NR DL Rank")).Where(v => Numeric(v, 1, 8).HasValue)), "distinct captured NR ranks.");
        Fill(report.Kpis, "NR modulation", Join(samples.Where(r => r.Technology == "5G").Select(r => r.Get("NR DL Modulation"))), "captured NR modulation labels.");
        foreach (var (label, alias) in new[]
        {
            ("q-RxLevMin", "qRxLevMin"), ("q-QualMin", "qQualMin"), ("q-Hyst", "qHyst"),
            ("s-IntraSearchP", "sIntraSearchP"), ("s-NonIntraSearchP", "sNonIntraSearchP"),
            ("threshServingLowP", "threshServingLowP"), ("Cell Reselection Priority", "cellReselectionPriority"),
            ("t-ReselectionNR", "tReselNR"), ("Common Subcarrier Spacing", "scsCommon"),
            ("SSB Subcarrier Offset", "ssbOffset"), ("CORESET#0 index", "coreset0"), ("SearchSpace#0 index", "ss0")
        }) Fill(report.Parameters, label, Join(Values(alias, label)), "explicitly captured parameter; no inference from signal measurements.");
    }

    private static double? Numeric(string? value, double min, double max)
    {
        if (!Available(value)) return null;
        var match = Regex.Match(value!, @"^\s*([-+]?\d+(?:\.\d+)?)(?:\s*(?:dBm|dB|Mbps|%)\s*)?$", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, Inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n : null;
    }
}
