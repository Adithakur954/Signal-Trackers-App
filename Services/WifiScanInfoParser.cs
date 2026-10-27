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

        string? Get(string name, string? alias = null)
        {
            if (fields.TryGetValue(name, out var rawValue) && Unquote(rawValue) is string value) return value;
            return alias != null && fields.TryGetValue(alias, out var alternate) ? Unquote(alternate) : null;
        }

        target.wifi_ssid = Get("SSID");
        target.wifi_bssid = Get("BSSID");
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
        target.wifi_interface_name = Get("interface name", "interfaceName");
        target.wifi_link_speed_mbps = Integer(Get("linkSpeed"));
        target.wifi_rx_link_speed_mbps = Integer(Get("rxLinkSpeed"));
        target.wifi_channel = Integer(Get("channel"));
        target.wifi_band = Get("band");
        target.wifi_signal_level_0_4 = Integer(Get("signalLevel"));
        target.wifi_matches_lock = Boolean(Get("matches_lock"));
        target.wifi_lock = Get("lock");
        target.wifi_plot_x = Number(Get("x"));
        target.wifi_plot_y = Number(Get("y"));
        target.wifi_plot_timestamp = Get("plot_timestamp");
        target.wifi_tap_timestamp = Get("tap_ts");
        target.wifi_drip_timestamp = Get("drip_ts");
        target.wifi_idle_before_s = Number(Get("idle_before_s"));
        target.wifi_interval_s = Number(Get("interval_s"));
        target.wifi_interpolation_on = Boolean(Get("interp_on"));
        target.wifi_zoom = Get("zoom");
        target.wifi_source_session = Get("plot_session");
        target.wifi_pin_size = Get("pin_size");
        target.wifi_nodata_mode = Get("nodata_mode");

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

    private static double? Number(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static bool? Boolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "1" or "supported" or "is supported" => true,
        "no" or "false" or "0" or "not supported" or "is not supported" => false,
        _ => null
    };

    private static string? Unquote(string? value)
    {
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null;
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
