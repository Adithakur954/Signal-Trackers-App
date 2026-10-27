using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using SignalTracker.Models;

namespace SignalTracker.Services;

public static class WifiPlotCsv
{
    public static bool IsFile(string? path) =>
        string.Equals(Path.GetFileName(path), "WiFi_plot.csv", StringComparison.OrdinalIgnoreCase);

    public static bool HasHeader(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!csv.Read() || !csv.ReadHeader()) return false;
        var headers = (csv.HeaderRecord ?? []).Select(value => value.Trim().Trim('\uFEFF')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return headers.Contains("timestamp") && headers.Contains("WiFi RSSI");
    }

    // Stream the plot once. Plot coordinates/session numbers are source metadata,
    // never replacements for geographic coordinates or the authorized target session.
    public static IEnumerable<tbl_network_log> Read(TextReader input, int sessionId, int companyId,
        Action? invalidRow = null)
    {
        using var csv = new CsvReader(input, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true, HeaderValidated = null, MissingFieldFound = null,
            BadDataFound = null, TrimOptions = TrimOptions.Trim, IgnoreBlankLines = true
        }, leaveOpen: true);
        if (!csv.Read() || !csv.ReadHeader()) yield break;
        var headers = csv.HeaderRecord ?? [];
        var map = headers.Select((header, index) => (header: header.Trim().Trim('\uFEFF'), index))
            .GroupBy(item => item.header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        if (!map.ContainsKey("timestamp") || !map.Keys.Any(key =>
                key.Equals("WiFi RSSI", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ssid", StringComparison.OrdinalIgnoreCase) || key.Equals("bssid", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("WiFi_plot.csv requires timestamp and Wi-Fi reading/SSID columns.");

        string? Field(string name) => map.TryGetValue(name, out var index) ? Empty(csv.GetField(index)) : null;
        while (csv.Read())
        {
            var rawTime = Field("timestamp");
            if (rawTime?.StartsWith('#') == true) continue; // Plot footer, e.g. '# COUNTS: Tapped ...'.
            if (!DateTime.TryParse(rawTime, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timestamp))
            {
                if (!string.IsNullOrWhiteSpace(rawTime)) invalidRow?.Invoke();
                continue;
            }
            var ssid = Field("ssid");
            var bssid = Field("bssid");
            var signal = Metric(Field("WiFi RSSI"));
            var channel = Metric(Field("Channel"));
            var band = Metric(Field("Band"));
            var payload = new StringBuilder("SSID: ").Append(JsonSerializer.Serialize(ssid))
                .Append(", BSSID: ").Append(JsonSerializer.Serialize(bssid));
            for (var index = 0; index < headers.Length; index++)
            {
                var header = headers[index].Trim().Trim('\uFEFF');
                if (header.Equals("ssid", StringComparison.OrdinalIgnoreCase) || header.Equals("bssid", StringComparison.OrdinalIgnoreCase)) continue;
                var value = Empty(csv.GetField(index));
                if (header.Equals("WiFi RSSI", StringComparison.OrdinalIgnoreCase) || header.Equals("Link Speed", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("Channel", StringComparison.OrdinalIgnoreCase) || header.Equals("Band", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("DL THPT", StringComparison.OrdinalIgnoreCase) || header.Equals("UL THPT", StringComparison.OrdinalIgnoreCase))
                    value = Metric(value);
                if (value == null) continue;
                payload.Append(", ").Append(Label(header)).Append(": ").Append(JsonSerializer.Serialize(value));
            }
            // Stable source representation retains subsecond time and every supplied column.
            payload.Append(", source: WiFi_plot.csv");
            yield return new tbl_network_log
            {
                session_id = sessionId, company_id = companyId, timestamp = timestamp,
                lat = Coordinate(Field("lat"), -90, 90), lon = Coordinate(Field("lon"), -180, 180),
                network = "WiFi", primary = "Yes", primary_cell_info_1 = payload.ToString(),
                m_alpha_short = ssid, m_alpha_long = bssid, rssi = Number(signal),
                level = Integer(Field("signal_level_0_4")), band = band, channel = channel,
                dl_tpt = Metric(Field("DL THPT")), ul_tpt = Metric(Field("UL THPT"))
            };
        }
    }

    private static string Label(string header) => header.ToLowerInvariant() switch
    {
        "timestamp" => "plot_timestamp", "wifi rssi" => "level", "link speed" => "linkSpeed",
        "rx_link_speed_mbps" => "rxLinkSpeed", "frequency_mhz" => "frequency",
        "signal_level_0_4" => "signalLevel", "session" => "plot_session",
        _ => header.Replace(':', '_').Replace(',', '_')
    };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "@" ? null : value.Trim();

    private static string? Metric(string? value)
    {
        if (value == null) return null;
        var colour = value.IndexOf("@#", StringComparison.Ordinal);
        return Empty(colour < 0 ? value : value[..colour]);
    }

    private static float? Number(string? value) => float.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var result) && float.IsFinite(result) ? result : null;
    private static int? Integer(string? value) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var result) ? result : null;
    private static float? Coordinate(string? value, float min, float max) => Number(value) is float number && number >= min && number <= max ? number : null;
}
