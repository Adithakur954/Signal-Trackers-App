using System.Globalization;
using SignalTracker.Services;

namespace SignalTracker.Models;

public sealed class L3SummaryFilters
{
    public string? Technology { get; set; }
    public string? Sources { get; set; }
    public string? Direction { get; set; }
    public string? Channel { get; set; }
    public string? Interface { get; set; }
    public string? Search { get; set; }
    public string? TimeFrom { get; set; }
    public string? TimeTo { get; set; }
    public bool FailuresOnly { get; set; }

    public string? Validate()
    {
        foreach (var value in new[] { TimeFrom, TimeTo }.Where(v => !string.IsNullOrWhiteSpace(v)))
            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                return "timeFrom/timeTo must be a time of day such as 12:15:30.000.";
        if (!string.IsNullOrWhiteSpace(Sources) && Sources.Split(',').Any(s => s.Trim().ToLowerInvariant() is not ("l3" or "event" or "all")))
            return "sources must contain l3, event, or all.";
        return null;
    }

    public bool Matches(L3ReportMessage row, bool failure)
    {
        static bool Match(string? filter, string value) => string.IsNullOrWhiteSpace(filter)
            || filter.Split(',').Any(f => f.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                || f.Trim().Equals(value, StringComparison.OrdinalIgnoreCase));
        if (!Match(Technology, row.Technology) || !Match(Sources, row.Source) || !Match(Direction, row.Direction)
            || !Match(Channel, row.Channel) || !Match(Interface, row.Interface) || (FailuresOnly && !failure)) return false;
        if (!string.IsNullOrWhiteSpace(Search) && !(row.Message + " " + row.Detail).Contains(Search, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(TimeFrom) && string.IsNullOrWhiteSpace(TimeTo)) return true;
        if (!TimeSpan.TryParse(row.Timestamp, CultureInfo.InvariantCulture, out var time))
        {
            if (!DateTime.TryParse(row.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
            time = date.TimeOfDay;
        }
        var from = string.IsNullOrWhiteSpace(TimeFrom) ? TimeSpan.Zero : TimeSpan.Parse(TimeFrom, CultureInfo.InvariantCulture);
        var to = string.IsNullOrWhiteSpace(TimeTo) ? TimeSpan.FromDays(1) : TimeSpan.Parse(TimeTo, CultureInfo.InvariantCulture);
        return from <= to ? time >= from && time <= to : time >= from || time <= to;
    }

    public override string ToString() => string.Join("; ", new[]
    {
        ("technology", Technology), ("sources", Sources), ("direction", Direction), ("channel", Channel),
        ("interface", Interface), ("search", Search), ("timeFrom", TimeFrom), ("timeTo", TimeTo),
        ("failuresOnly", FailuresOnly ? "true" : null)
    }.Where(p => !string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1 + "=" + p.Item2));
}
