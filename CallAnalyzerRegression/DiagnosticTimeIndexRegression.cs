using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SignalTracker.Controllers;

internal static class DiagnosticTimeIndexRegression
{
    public static void Run()
    {
        var controller = typeof(MapViewController);
        var rowType = controller.GetNestedType("DiagnosticL3Row", BindingFlags.NonPublic)!;
        var indexType = controller.GetNestedType("DiagnosticL3TimeIndex", BindingFlags.NonPublic)!;
        var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType))!;
        var reference = new List<(object Row, int? Session, TimeSpan? Time)>();
        void Add(int? session, string? timestamp)
        {
            var row = Activator.CreateInstance(rowType)!;
            rowType.GetProperty("SessionId")!.SetValue(row, session);
            rowType.GetProperty("TimestampText")!.SetValue(row, timestamp);
            rows.Add(row);
            reference.Add((row, session, (TimeSpan?)rowType.GetProperty("EventTime")!.GetValue(row)));
        }
        Add(1, "invalid");
        Add(1, "00:00:05");
        Add(1, "00:00:05"); // Equal timestamp: retain original input order.
        Add(1, "23:59:50");
        Add(2, "12:00:00");
        Add(null, "12:00:00");
        Add(0, "13:00:00"); // Null session must not collide with session zero.
        Add(3, null);
        var random = new Random(42);
        for (var i = 0; i < 6000; i++)
            Add(random.Next(1, 5), TimeSpan.FromSeconds(random.Next(86400)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));

        var index = Activator.CreateInstance(indexType, [rows])!;
        var find = indexType.GetMethod("Find")!;
        object? Slow(int? session, TimeSpan? time) => reference.Where(row => row.Session == session)
            .Select(row => (row.Row, Distance: time.HasValue && row.Time.HasValue
                ? Math.Abs((row.Time.Value - time.Value).TotalSeconds < 0
                    ? (row.Time.Value - time.Value).TotalSeconds + 86400
                    : (row.Time.Value - time.Value).TotalSeconds) : double.MaxValue))
            .Where(row => row.Distance <= 30 || !time.HasValue)
            .OrderBy(row => row.Distance).Select(row => row.Row).FirstOrDefault();
        var queries = new List<(int? Session, TimeSpan? Time)>();
        foreach (var session in new int?[] { null, 0, 1, 2, 3, 4, 999 })
            foreach (var text in new[] { "00:00:00", "00:00:05", "11:59:30", "11:59:29", "12:00:00", "12:00:01", "23:59:40", "23:59:59" })
                queries.Add((session, TimeSpan.Parse(text, CultureInfo.InvariantCulture)));
        foreach (var session in new int?[] { null, 0, 1, 3, 999 }) queries.Add((session, null));
        for (var i = 0; i < 1000; i++) queries.Add((random.Next(1, 5), TimeSpan.FromSeconds(random.Next(86400))));
        var timer = Stopwatch.StartNew();
        var expected = queries.Select(q => Slow(q.Session, q.Time)).ToArray();
        var referenceMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (var i = 0; i < queries.Count; i++)
        {
            var actual = find.Invoke(index, [queries[i].Session, queries[i].Time]);
            if (!ReferenceEquals(actual, expected[i]))
                throw new InvalidOperationException($"Indexed lookup changed result for {queries[i]}.");
        }
        Console.WriteLine($"Diagnostic time index: {queries.Count} comparisons passed, including midnight, missing times, session isolation and duplicate timestamps. Reference scan: {referenceMs:F1} ms; indexed: {timer.Elapsed.TotalMilliseconds:F1} ms (synthetic lookup only).");
    }
}
