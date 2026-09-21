namespace SignalTracker.Controllers;

public partial class MapViewController
{
    // Request-local index. Preserve the existing forward, midnight-wrapping
    // 30-second lookup and the input-order tie break for equal timestamps.
    private sealed class DiagnosticL3TimeIndex
    {
        private readonly Dictionary<long, (DiagnosticL3Row First, DiagnosticL3Row[] Timed)> sessions;

        public DiagnosticL3TimeIndex(IReadOnlyList<DiagnosticL3Row> rows)
        {
            sessions = rows.GroupBy(row => SessionKey(row.SessionId)).ToDictionary(group => group.Key,
                group => (group.First(), group.Where(row => row.EventTime.HasValue)
                    .OrderBy(row => row.EventTime).ToArray()));
        }

        private static long SessionKey(int? sessionId) => sessionId.HasValue ? sessionId.Value : long.MinValue;

        public DiagnosticL3Row? Find(int? sessionId, TimeSpan? time)
        {
            if (!sessions.TryGetValue(SessionKey(sessionId), out var session)) return null;
            if (!time.HasValue) return session.First;
            var rows = session.Timed;
            if (rows.Length == 0) return null;
            var low = 0;
            var high = rows.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (rows[middle].EventTime!.Value < time.Value) low = middle + 1;
                else high = middle;
            }
            var candidate = rows[low == rows.Length ? 0 : low];
            return Math.Abs(SecondsBetween(time.Value, candidate.EventTime!.Value)) <= 30 ? candidate : null;
        }
    }
}
