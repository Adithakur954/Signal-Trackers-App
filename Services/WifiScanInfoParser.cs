using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SignalTracker.Models;

namespace SignalTracker.Services;

public static class WifiScanInfoParser
{
    private static readonly Regex RadioChain = new(
        @"\bRadioChainInfo\s*:\s*id\s*=\s*(?<id>[+-]?\d{1,11})\s*,\s*level\s*=\s*(?<level>[+-]?\d{1,11})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static void Apply(WifiNetworkLogFields target, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var fields = ReadFields(raw);
        // A Wi-Fi session can also contain cellular diagnostics. Do not interpret those as scan data.
        if (!fields.ContainsKey("SSID") && !fields.ContainsKey("BSSID")) return;

        string? Get(params string[] names) => names.Select(name => fields.GetValueOrDefault(name))
            .FirstOrDefault(value => value != null);

        target.wifi_ssid = Unquote(Get("SSID"));
        target.wifi_bssid = Unquote(Get("BSSID"));
        target.wifi_capabilities = Get("capabilities");
        target.wifi_rssi_dbm = Integer(Get("level"));
        target.wifi_frequency_mhz = Integer(Get("frequency"));
        target.wifi_timestamp = long.TryParse(Get("timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) ? timestamp : null;
        target.wifi_distance = Get("distance");
        target.wifi_distance_sd = Get("distanceSd");
        target.wifi_passpoint = Boolean(Get("passpoint"));
        // Preserve the captured bandwidth enum and standard text without guessing their interpretation.
        target.wifi_channel_bandwidth = Integer(Get("ChannelBandwidth", "channelWidth"));
        target.wifi_center_freq0_mhz = Integer(Get("centerFreq0"));
        target.wifi_center_freq1_mhz = Integer(Get("centerFreq1"));
        target.wifi_standard = Get("standard", "wifiStandard");
        target.wifi_80211mc_responder = Boolean(Get("80211mcResponder"));
        target.wifi_80211az_ntb_responder = Boolean(Get("80211azNtbResponder"));
        target.wifi_twt_responder = Boolean(Get("TWT Responder"));
        target.wifi_interface_name = Unquote(Get("interface name", "interfaceName"));

        var chains = Get("Radio Chain Infos", "radioChainInfos");
        target.wifi_radio_chain_infos = null;
        if (chains != null && chains.StartsWith('[') && chains.EndsWith(']'))
        {
            var parsed = new List<WifiRadioChainInfo>();
            foreach (Match match in RadioChain.Matches(chains))
                if (Integer(match.Groups["id"].Value) is int id && Integer(match.Groups["level"].Value) is int level)
                    parsed.Add(new() { id = id, level = level });
            // An explicitly empty array is different from an absent/unreadable value.
            if (parsed.Count > 0 || string.IsNullOrWhiteSpace(chains[1..^1]))
                target.wifi_radio_chain_infos = parsed;
        }
    }

    private static int? Integer(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool? Boolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "1" or "supported" or "is supported" => true,
        "no" or "false" or "0" or "not supported" or "is not supported" => false,
        _ => null
    };

    private static string? Unquote(string? value)
    {
        if (value == null || value.Length < 2) return value;
        if (value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value); }
            catch (JsonException) { return value[1..^1]; }
        }
        return value[0] == '\'' && value[^1] == '\'' ? value[1..^1] : value;
    }

    private static Dictionary<string, string> ReadFields(string raw)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i == raw.Length || (raw[i] == ',' && depth == 0 && quote == '\0'))
            {
                var part = raw.AsSpan(start, i - start).Trim();
                var colon = part.IndexOf(':');
                if (colon > 0) fields[part[..colon].Trim().ToString()] = part[(colon + 1)..].Trim().ToString();
                start = i + 1;
                continue;
            }
            var character = raw[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            // Quotes delimit a value only immediately after its field colon, not an apostrophe in an unquoted SSID.
            if (character is '"' or '\'')
            {
                var prefix = raw.AsSpan(start, i - start).TrimEnd();
                if (prefix.Length > 0 && prefix[^1] == ':') quote = character;
            }
            else if (character == '[') depth++;
            else if (character == ']' && depth > 0) depth--;
        }
        return fields;
    }
}
