using System.Text.Json;
using System.Text.Json.Nodes;

namespace SignalTracker.Services;

/// <summary>
/// Report Acceptance, stored per user in <c>thresholds.report_acceptance_json</c>.
///
/// One block per technology (2g, 3g, 4g, 5g), each holding ONLY the metrics that
/// technology reports under its own names (2G RxLev / RxQual, 3G RSCP / Ec/No,
/// 4G RSRP / RSRQ / SINR, 5G nrRSRP / nrRSRQ / nrSINR, plus MOS, DL, UL, latency,
/// jitter and packet loss). There is deliberately no shared "default" block: a
/// technology never borrows another technology's metric or numbers.
///
/// Every metric has two edges and a direction:
///   higher is better: value &gt;= good -> Good, value &gt;= poor -> Fair, else Poor
///   lower  is better: value &lt;= good -> Good, value &lt;= poor -> Fair, else Poor
/// PASS is "Fair or better", i.e. the poor edge is the pass/fail cutoff
/// (RSRP poor = -105: -104 passes, -106 fails).
/// good_share / poor_share (RSRP, RSRQ, SINR, MOS only) carry the "more than 90 %
/// of samples" / "under 75 %" rules of the Good and Poor criteria.
/// </summary>
public static class ReportAcceptanceDefaults
{
    public const int CurrentVersion = 2;

    public static readonly string[] Technologies = { "2g", "3g", "4g", "5g" };

    private sealed record Spec(
        string Key, string Label, string Unit, bool Higher, double Good, double Poor,
        double? GoodShare = null, double? PoorShare = null);

    private static IEnumerable<Spec> Specs(string tech)
    {
        switch (tech)
        {
            case "2g":
                yield return new("rxlev", "RxLev", "dBm", true, -95, -105, 90, 75);
                yield return new("rxqual", "RxQual", "", false, 2, 4);
                break;
            case "3g":
                yield return new("rscp", "RSCP", "dBm", true, -95, -105, 90, 75);
                yield return new("ecno", "Ec/No", "dB", true, -10, -15, 90, 75);
                yield return new("ecno_derived", "Ec/No-derived", "dB", true, 20, 13, 90, 75);
                break;
            case "4g":
                yield return new("rsrp", "RSRP", "dBm", true, -95, -105, 90, 75);
                yield return new("rsrq", "RSRQ", "dB", true, -10, -15, 90, 75);
                yield return new("sinr", "SINR", "dB", true, 20, 13, 90, 75);
                break;
            case "5g":
                yield return new("nr_rsrp", "nrRSRP", "dBm", true, -95, -105, 90, 75);
                yield return new("nr_rsrq", "nrRSRQ", "dB", true, -10, -15, 90, 75);
                yield return new("nr_sinr", "nrSINR", "dB", true, 20, 13, 90, 75);
                break;
        }

        yield return new("mos", "MOS", "", true, 4.0, 3.0, 90, 75);

        // Technology-specific service numbers: a 2G drive can never reach 4G speeds.
        var (dlGood, dlPoor, ulGood, ulPoor) = tech switch
        {
            "2g" => (0.1, 0.05, 0.05, 0.02),
            "3g" => (3.0, 1.0, 1.0, 0.3),
            "4g" => (15.0, 10.0, 5.0, 1.0),
            _ => (100.0, 50.0, 20.0, 10.0),
        };
        var (latGood, latPoor) = tech switch
        {
            "2g" => (200.0, 500.0),
            "3g" => (100.0, 200.0),
            _ => (50.0, 100.0),
        };
        var (jitGood, jitPoor) = tech switch
        {
            "2g" => (50.0, 100.0),
            "3g" => (30.0, 75.0),
            _ => (20.0, 50.0),
        };

        yield return new("dl_throughput", "DL Throughput", "Mbps", true, dlGood, dlPoor);
        yield return new("ul_throughput", "UL Throughput", "Mbps", true, ulGood, ulPoor);
        yield return new("latency", "Latency", "ms", false, latGood, latPoor);
        yield return new("jitter", "Jitter", "ms", false, jitGood, jitPoor);
        yield return new("packet_loss", "Packet Loss", "%", false, 1, 3);
    }

    private static JsonObject MetricNode(Spec s)
    {
        var node = new JsonObject
        {
            ["label"] = s.Label,
            ["unit"] = s.Unit,
            ["direction"] = s.Higher ? "higher" : "lower",
            ["good"] = s.Good,
            ["poor"] = s.Poor,
        };
        if (s.GoodShare.HasValue) node["good_share"] = s.GoodShare.Value;
        if (s.PoorShare.HasValue) node["poor_share"] = s.PoorShare.Value;
        return node;
    }

    private static JsonObject BuildTechnology(string tech)
    {
        var metrics = new JsonObject();
        foreach (var spec in Specs(tech))
            metrics[spec.Key] = MetricNode(spec);
        return metrics;
    }

    public static JsonObject Build()
    {
        var root = new JsonObject { ["version"] = CurrentVersion };
        foreach (var tech in Technologies)
            root[tech] = BuildTechnology(tech);
        return root;
    }

    public static string BuildJson() => Build().ToJsonString();

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value) && double.IsFinite(value);
    }

    /// <summary>True when the text is a version-2 document (valid or not).</summary>
    public static bool LooksCurrent(string? json)
    {
        var root = Parse(json);
        return root?["version"] is JsonValue v && v.TryGetValue<int>(out var version) && version == CurrentVersion;
    }

    /// <summary>Structural and numeric validation of every metric that is present.</summary>
    public static bool TryValidate(string? json, out string error)
    {
        error = string.Empty;
        var root = Parse(json);
        if (root == null)
        {
            error = "Report acceptance must be a JSON object.";
            return false;
        }
        if (!LooksCurrent(json))
        {
            error = $"Report acceptance must be version {CurrentVersion}.";
            return false;
        }

        foreach (var tech in Technologies)
        {
            if (root[tech] == null)
                continue; // a missing technology is filled from the defaults, never from another technology
            if (root[tech] is not JsonObject metrics)
            {
                error = $"'{tech}' must be an object of metrics.";
                return false;
            }

            foreach (var (key, node) in metrics)
            {
                var where = $"{tech.ToUpperInvariant()} {key}";
                if (node is not JsonObject metric)
                {
                    error = $"{where} must be an object.";
                    return false;
                }
                if (!TryNumber(metric["good"], out var good) || !TryNumber(metric["poor"], out var poor))
                {
                    error = $"{where}: 'good' and 'poor' must be numbers.";
                    return false;
                }

                var direction = metric["direction"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null;
                if (direction == "higher")
                {
                    if (good <= poor)
                    {
                        error = $"{where}: for a higher-is-better metric the Good edge must be above the Poor edge.";
                        return false;
                    }
                }
                else if (direction == "lower")
                {
                    if (good >= poor)
                    {
                        error = $"{where}: for a lower-is-better metric the Good edge must be below the Poor edge.";
                        return false;
                    }
                }
                else
                {
                    error = $"{where}: direction must be 'higher' or 'lower'.";
                    return false;
                }

                foreach (var shareKey in new[] { "good_share", "poor_share" })
                {
                    if (metric[shareKey] == null)
                        continue;
                    if (!TryNumber(metric[shareKey], out var share) || share < 0 || share > 100)
                    {
                        error = $"{where}: {shareKey} must be a number from 0 to 100.";
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public static bool IsCurrent(string? json) => TryValidate(json, out _);

    /// <summary>
    /// Adds any technology or metric that is missing, copying it from the SAME
    /// technology's defaults. Returns the input unchanged when nothing is missing.
    /// </summary>
    public static string FillMissing(string json)
    {
        var root = Parse(json);
        if (root == null)
            return BuildJson();

        var seed = Build();
        var changed = false;
        foreach (var tech in Technologies)
        {
            var seedMetrics = (JsonObject)seed[tech]!;
            if (root[tech] is not JsonObject metrics)
            {
                root[tech] = seedMetrics.DeepClone();
                changed = true;
                continue;
            }
            foreach (var (key, node) in seedMetrics)
            {
                if (!metrics.ContainsKey(key))
                {
                    metrics[key] = node!.DeepClone();
                    changed = true;
                }
            }
        }
        return changed ? root.ToJsonString() : json;
    }

    /// <summary>
    /// The old flat, free-text shape cannot be turned into numbers reliably, so it is
    /// replaced by the new defaults and kept verbatim under "legacy" (ignored by the app).
    /// </summary>
    public static string Upgrade(string? oldJson)
    {
        var root = Build();
        if (!string.IsNullOrWhiteSpace(oldJson))
            root["legacy"] = oldJson;
        return root.ToJsonString();
    }
}
