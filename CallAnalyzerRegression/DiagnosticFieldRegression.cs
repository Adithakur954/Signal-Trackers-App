using System.Reflection;
using SignalTracker.Controllers;

internal static class DiagnosticFieldRegression
{
    internal static void Run()
    {
        Check("column precedence", new() { ["channel"] = " PCCH ", ["direction"] = " DL ", ["cause"] = " supplied ",
            ["detail"] = "UL-DCCH | cause=otherFailure" }, "PCCH", "DL", "supplied");
        Check("missing columns", new() { ["detail"] = "packet | UL-DCCH | cause=otherFailure" }, "UL-DCCH", "UL", "otherFailure");
        Check("blank columns", new() { ["channel"] = " ", ["direction"] = null, ["cause"] = "",
            ["detail"] = "packet | DL-DCCH | emmCause : 0x13 ESM failure" }, "DL-DCCH", "DL", "0x13 ESM failure");
        Check("complete broadcast channel", new() { ["detail"] = "packet | BCCH-DL-SCH" }, "BCCH-DL-SCH", "DL", null);
        Check("NR channel label", new() { ["detail"] = "Channel Name: \"NR-UL-DCCH\"" }, "NR-UL-DCCH", "UL", null);
        Check("underscore channel", new() { ["detail"] = "NR_DL_DCCH" }, "NR_DL_DCCH", "DL", null);
        Check("NAS direction and cause", new() { ["detail"] = "LTE NAS (ESM) DL | esmCause : 0x21 cause 0x21" }, null, "DL", "0x21 cause 0x21");
        Check("event no cause", new() { ["detail"] = "{.cause = NONE, .active = DORMANT}" }, null, null, "NONE");
        Check("zero reject cause", new() { ["detail"] = "rejectCause=0 emergencyEnabled=false" }, null, null, "0");
        Check("unknown values", new() { ["detail"] = "Idle (ended)" }, null, null, null);
        Check("ambiguous direction", new() { ["detail"] = "UL and DL activity" }, null, null, null);
        Check("explicit detail labels", new() { ["detail"] = "channel=PCCH | direction=DL | cause=other" }, "PCCH", "DL", "other");
        Check("blank primary with alias", new() { ["channel"] = "", ["Channel Name"] = " PCCH ", ["DIR"] = " DL ", ["emmCause"] = "19" }, "PCCH", "DL", "19");
        Check("call disconnect formatting", new() { ["event"] = "CALL_DISCONNECT_NONZERO_CAUSE", ["detail"] = "cause=2" },
            null, null, "2", "2 - NORMAL (Remote hangup)");
        Console.WriteLine("Diagnostic field regressions passed for both L3/Event builders.");
    }

    private static void Check(string name, Dictionary<string, object?> row, string? channel, string? direction, string? cause, string? eventCause = null)
    {
        foreach (var isL3 in new[] { true, false })
        {
            var result = Build(isL3, row);
            var actual = (Value(result, "Channel"), Value(result, "Direction"), Value(result, "Cause"));
            var expected = (channel, direction, isL3 ? cause : eventCause ?? cause);
            if (actual != expected)
                throw new InvalidOperationException($"{name} ({(isL3 ? "L3" : "Event")}): expected {expected}, got {actual}");
        }
    }

    internal static object Build(bool isL3, Dictionary<string, object?> row)
    {
        var method = typeof(L3EventController).GetMethod(isL3 ? "BuildL3DiagnosticInsertRow" : "BuildEventDiagnosticInsertRow",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, isL3
            ? new object?[] { 1, 1, "sample.csv", 1, "csv", row, null }
            : new object?[] { 1, 1, "sample.csv", 1, row })!;
    }

    internal static string? Value(object result, string field) => (string?)result.GetType().GetProperty(field)!.GetValue(result);
}
