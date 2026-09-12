using System.Globalization;
using System.Text.RegularExpressions;

namespace SignalTracker.Helper;

internal static class DiagnosticFieldParser
{
    internal sealed record Fields(string? Channel, string? Direction, string? Cause);

    private static readonly string[] CausePatterns =
    {
        @"\b(?:esmCause|emmCause)\s*[:=]\s*(?<cause>[^|,;\r\n]+)",
        @"\bgetDisconnectCause\s*:\s*cause\s*=\s*(?<cause>[^|,;\s}\]\)]+)",
        @"\b(?:disconnectCause|releaseCause|failureCause|rejectCause|restrictCause|mRestrictCause|establishmentCause|reestablishmentCause)\s*[:=]\s*(?<cause>[^|,;\s}\]\)]+)",
        @"(?:^|[^\w])\.?cause\s*[:=]\s*(?<cause>[^|,;\s}\]\)]+)"
    };

    internal static Fields Resolve(
        IDictionary<string, object?> row, string? detail, string? rawText = null, string? eventCause = null)
    {
        // Normalize each header once, rather than repeating regex work for every alias.
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in row)
        {
            var value = Convert.ToString(item.Value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;
            var key = Regex.Replace(item.Key, @"[^\w]+", "_").Trim('_');
            columns.TryAdd(key, value);
        }
        var channel = Column(columns, "channel", "chan", "channel_name", "channel_number", "earfcn")
            ?? ExtractChannel(detail, rawText);
        var direction = Column(columns, "direction", "dir", "call_direction")
            ?? ExtractDirection(detail, rawText, channel);
        var cause = Column(columns, "cause", "esm_cause", "esmCause", "emm_cause", "emmCause",
                "disconnect_cause", "disconnectCause", "release_cause", "releaseCause",
                "failure_cause", "failureCause", "reject_cause", "rejectCause")
            ?? eventCause
            ?? ExtractCause(detail, rawText);
        return new Fields(channel, direction, cause);
    }

    private static string? Column(Dictionary<string, string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            if (columns.TryGetValue(name, out var value)) return value;
        }
        return null;
    }

    private static string? ExtractChannel(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var label = Regex.Match(text,
                @"\b(?:channel(?:[ _]name)?|chan)\s*[:=]\s*[""']?(?<channel>[A-Za-z0-9][A-Za-z0-9_-]*)",
                RegexOptions.IgnoreCase);
            if (label.Success) return label.Groups["channel"].Value;

            // Match the complete channel token, including NR and transport suffixes.
            var match = Regex.Match(text,
                @"(?<![A-Za-z0-9_])(?:NR[-_])?(?:[DU]L[-_])?(?:BCCH(?:[-_](?:DL[-_]SCH|BCH|SCH))?|PCCH|CCCH|DCCH|DTCH|BCH|PCH|SCH)(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase);
            if (match.Success) return match.Value.ToUpperInvariant();
        }
        return null;
    }

    private static string? ExtractDirection(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var label = Regex.Match(text,
                @"\b(?:direction|dir|call_direction)\s*[:=]\s*[""']?(?<direction>[A-Za-z][A-Za-z_-]*)",
                RegexOptions.IgnoreCase);
            if (label.Success) return label.Groups["direction"].Value;
        }
        var directions = texts.Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => Regex.Matches(text!, @"(?<![A-Za-z0-9])(?:UL|DL)(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            .Select(match => match.Value.ToUpperInvariant()).Distinct().ToArray();
        // A detail containing both UL and DL does not establish one direction.
        return directions.Length == 1 ? directions[0] : null;
    }

    private static string? ExtractCause(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var pattern in CausePatterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var cause = Regex.Replace(match.Groups["cause"].Value, @"\s+", " ")
                    .Trim(' ', '.', ',', ';', '|', '}', ']', ')', '"', '\'');
                if (!string.IsNullOrWhiteSpace(cause))
                    return cause.Length > 255 ? cause[..255] : cause;
            }
        }
        return null;
    }
}
