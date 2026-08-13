namespace SignalTracker.Controllers;

public sealed class DiagnosticAnalyzerInput
{
    public int SessionId { get; init; } = 1;
    public string? Timestamp { get; init; }
    public string? Category { get; init; }
    public string? Name { get; init; }
    public string? Detail { get; init; }
    public string? Source { get; init; }
}

public sealed class DiagnosticCallRegressionRow
{
    public string Id { get; init; } = string.Empty;
    public string? Start { get; init; }
    public string? Alerting { get; init; }
    public string? End { get; init; }
    public string Result { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class DiagnosticCallRegressionResult
{
    public List<DiagnosticCallRegressionRow> Calls { get; init; } = new();
    public int Connected => Calls.Count(call => call.Result == "Connected");
    public int NotConnected => Calls.Count(call => call.Result == "Not Connected");
    public int Dropped => Calls.Count(call => call.Result == "Dropped");
    public int Unknown => Calls.Count(call => call.Result == "Unknown");
}
