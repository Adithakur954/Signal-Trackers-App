param(
    [string]$Url = "http://localhost:5224/health/ready",
    [int]$Concurrency = 10,
    [int]$RequestsPerWorker = 20,
    [string]$BearerToken = "",
    [string]$Cookie = ""
)

$source = @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

public sealed class LoadResultRow
{
    public int Status { get; set; }
    public bool Ok { get; set; }
    public double Ms { get; set; }
    public long Bytes { get; set; }
    public string Error { get; set; }
}

public static class LocalLoadTester
{
    public static LoadResultRow[] Run(string url, int concurrency, int requestsPerWorker, string bearerToken, string cookie)
    {
        var handler = new HttpClientHandler();
        using (var client = new HttpClient(handler))
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie);

            var bag = new ConcurrentBag<LoadResultRow>();
            var tasks = new List<Task>();
            for (var worker = 0; worker < concurrency; worker++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (var i = 0; i < requestsPerWorker; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        var row = new LoadResultRow();
                        try
                        {
                            using (var response = await client.GetAsync(url).ConfigureAwait(false))
                            {
                                var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                row.Status = (int)response.StatusCode;
                                row.Ok = row.Status >= 200 && row.Status < 400;
                                row.Bytes = body.LongLength;
                            }
                        }
                        catch (Exception ex)
                        {
                            row.Error = ex.Message;
                        }
                        finally
                        {
                            sw.Stop();
                            row.Ms = sw.Elapsed.TotalMilliseconds;
                            bag.Add(row);
                        }
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray());
            return bag.ToArray();
        }
    }
}
"@

Add-Type -TypeDefinition $source -ReferencedAssemblies 'System.Net.Http.dll'
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()
$rows = [LocalLoadTester]::Run($Url, $Concurrency, $RequestsPerWorker, $BearerToken, $Cookie)
$swTotal.Stop()
$latencies = @($rows | Sort-Object Ms | ForEach-Object { [double]$_.Ms })
function Percentile([double[]]$values, [double]$p) {
    if ($values.Count -eq 0) { return 0 }
    $rank = [Math]::Ceiling(($p / 100.0) * $values.Count) - 1
    $rank = [Math]::Max(0, [Math]::Min($rank, $values.Count - 1))
    return [Math]::Round($values[$rank], 2)
}
$success = @($rows | Where-Object Ok).Count
$errors = $rows.Count - $success
$grouped = @($rows | Group-Object Status | Sort-Object Name | ForEach-Object { [pscustomobject]@{ Status = $_.Name; Count = $_.Count } })
$result = [pscustomobject]@{
    Url = $Url
    Concurrency = $Concurrency
    TotalRequests = $rows.Count
    Success = $success
    Errors = $errors
    ErrorRatePct = if ($rows.Count) { [Math]::Round(($errors * 100.0) / $rows.Count, 2) } else { 0 }
    TotalSeconds = [Math]::Round($swTotal.Elapsed.TotalSeconds, 2)
    RequestsPerSecond = if ($swTotal.Elapsed.TotalSeconds -gt 0) { [Math]::Round($rows.Count / $swTotal.Elapsed.TotalSeconds, 2) } else { 0 }
    AvgMs = if ($latencies.Count) { [Math]::Round(($latencies | Measure-Object -Average).Average, 2) } else { 0 }
    P50Ms = Percentile $latencies 50
    P95Ms = Percentile $latencies 95
    P99Ms = Percentile $latencies 99
    MinMs = if ($latencies.Count) { [Math]::Round($latencies[0], 2) } else { 0 }
    MaxMs = if ($latencies.Count) { [Math]::Round($latencies[-1], 2) } else { 0 }
    StatusCounts = $grouped
    SampleErrors = @($rows | Where-Object Error | Select-Object -First 5 -ExpandProperty Error)
}
$result | ConvertTo-Json -Depth 5

