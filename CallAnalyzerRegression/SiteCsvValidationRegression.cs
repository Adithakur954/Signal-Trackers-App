using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SignalTracker.Controllers;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class SiteCsvValidationRegression
{
    public static async Task RunAsync(string templatePath)
    {
        static void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
        }

        var lines = await File.ReadAllLinesAsync(templatePath);
        var header = lines[0];
        var headers = header.Split(',');
        var optionalHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "site_name", "tac", "bw", "maximum_transmission_power_of_resource",
            "real_transmit_power_of_resource", "reference_signal_power", "frequency"
        };
        var requiredHeaders = headers.Where(h => !optionalHeaders.Contains(h)).ToArray();
        Check(headers.Length == 21, "Review the site schema when the template columns change.");
        Check(requiredHeaders.Length == 14, "Preserve the original 14 required site headers.");
        Check(SiteCsvSchema.ValidateHeader(header, out _) == null, "Actual template must be accepted.");
        Check(SiteCsvSchema.ValidateHeader(string.Join(',', headers.Reverse()), out _) == null, "Column order may change.");
        var normalizedHeader = string.Join(',', headers.Select(h => $"\" {h.ToUpperInvariant()} \""));
        Check(SiteCsvSchema.ValidateHeader("\uFEFF" + header, out _) == null, "BOM must be accepted.");
        Check(SiteCsvSchema.ValidateHeader(normalizedHeader, out _) == null, "Quoted, padded headers must be accepted.");
        Check(SiteCsvSchema.ValidateHeader(header + ",site", out _)?.Contains("Duplicate") == true, "Duplicate columns must fail.");
        Check(SiteCsvSchema.ValidateHeader("", out _) != null, "Empty header must fail.");

        foreach (var optional in optionalHeaders)
            Check(SiteCsvSchema.ValidateHeader(string.Join(',', headers.Where(h => h != optional)), out _) == null,
                $"Missing optional column {optional} must be accepted.");
        var requiredOnlyHeader = string.Join(',', requiredHeaders);
        Check(SiteCsvSchema.ValidateHeader(requiredOnlyHeader, out _) == null, "All optional columns may be absent together.");

        var sampleValues = lines[1].Split(',');
        using (var reader = new StringReader(requiredOnlyHeader + "\n" + string.Join(',',
            headers.Select((name, index) => (name, index)).Where(h => !optionalHeaders.Contains(h.name)).Select(h => sampleValues[h.index]))))
        using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => SiteCsvSchema.NormalizeHeader(args.Header),
            MissingFieldFound = null,
            HeaderValidated = null
        }))
        {
            var row = csv.GetRecords<ProcessCSVController.SitePredictionCsvModel>().Single();
            foreach (var optional in optionalHeaders)
                Check(row.GetType().GetProperty(optional)?.GetValue(row) == null, $"Absent {optional} must stay null.");
            Check(row.site == sampleValues[0], "Required site data must still be read.");
        }

        // Verify accepted case/whitespace variants also populate the actual import model.
        using (var reader = new StringReader(normalizedHeader + "\n" + lines[1]))
        using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => SiteCsvSchema.NormalizeHeader(args.Header),
            MissingFieldFound = null,
            HeaderValidated = null
        }))
        {
            var row = csv.GetRecords<ProcessCSVController.SitePredictionCsvModel>().Single();
            var values = lines[1].Split(',');
            for (var i = 0; i < headers.Length; i++)
                Check((string?)row.GetType().GetProperty(headers[i])?.GetValue(row) == values[i],
                    $"Template column {headers[i]} must reach the import model.");
        }

        var output = Path.GetFullPath(Path.Combine("artifacts", "site-csv-regression", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        var accessor = new HttpContextAccessor();
        var redis = new RedisService(null);
        var importer = new ProcessCSVController(db, new CommonFunction(db, accessor), redis);
        // These services must not be accessed before header validation rejects the request.
        var map = new MapViewController(db, accessor, null!, redis, new UserScopeService(accessor),
            null!, new NetworkLogDataService(), new ConfigurationBuilder().Build());

        foreach (var missingColumn in requiredHeaders)
        {
            var incomplete = string.Join(',', headers.Where(h => h != missingColumn));
            var error = SiteCsvSchema.ValidateHeader(incomplete, out _);
            Check(error?.Contains(missingColumn, StringComparison.Ordinal) == true, $"Missing {missingColumn} must be named.");
            var path = Path.Combine(output, missingColumn + ".csv");
            await File.WriteAllTextAsync(path, incomplete + "\n");
            int inserted = 0, updated = 0;
            Check(!importer.ProcessSitePredictionSheet(path, 0, 1, ref inserted, ref updated, out var errors, []),
                $"Import must reject missing {missingColumn} before database access.");
            Check(inserted == 0 && errors.Any(e => e.Contains(missingColumn)), "Import must return the header reason and insert nothing.");
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(incomplete + "\n"));
            var result = await map.UploadSitePredictionCsv(new MapViewController.UploadSitePredictionRequest
            {
                ProjectId = 1,
                File = new FormFile(stream, 0, stream.Length, "File", "site.csv")
            });
            Check(result is BadRequestObjectResult bad && JsonSerializer.Serialize(bad.Value).Contains(missingColumn),
                "Map upload must return HTTP 400 with the missing column.");
        }

        var missingBoth = string.Join(',', headers.Where(h => h is not "site" and not "cell_id"));
        var bothError = SiteCsvSchema.ValidateHeader(missingBoth, out _);
        Check(bothError?.Contains("site, cell_id") == true, "Report all missing required columns together; site_name cannot replace site.");

        var optionalZip = Path.Combine(output, "required-only.zip");
        using (var archive = ZipFile.Open(optionalZip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("required-only.csv").Open());
            writer.WriteLine(requiredOnlyHeader);
        }
        Check(SiteCsvSchema.ValidateUpload(optionalZip, isZip: true) == null, "ZIP headers may omit every optional column.");

        foreach (var invalidFirst in new[] { true, false })
        {
            var path = Path.Combine(output, $"mixed-{invalidFirst}.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var invalid in new[] { invalidFirst, !invalidFirst })
                {
                    using var writer = new StreamWriter(archive.CreateEntry(invalid ? "invalid.csv" : "valid.csv").Open());
                    writer.WriteLine(invalid ? missingBoth : header);
                    writer.WriteLine(lines[1]);
                }
            }
            Check(!importer.ProcessFile(15, 0, path, Path.GetFileName(path), null, 1, "", out var error),
                "A ZIP with any invalid site CSV must fail before database access, regardless of entry order.");
            Check(error.Contains("invalid.csv") && error.Contains("site, cell_id"), "ZIP failure must name the file and missing columns.");
        }
        var oversizedHeader = new string('x', SiteCsvSchema.MaxHeaderCharacters + 100);
        using (var boundedReader = new StringReader(oversizedHeader + "\n"))
        {
            var boundedHeader = SiteCsvSchema.ReadBoundedHeader(boundedReader);
            Check(boundedHeader!.Length == SiteCsvSchema.MaxHeaderCharacters + 1, "Header read must stop at the bound.");
            Check(SiteCsvSchema.ValidateHeader(boundedHeader, out _) != null, "Oversized header must be rejected.");
        }
        var tooManyEntries = Path.Combine(output, "too-many-entries.zip");
        using (var archive = ZipFile.Open(tooManyEntries, ZipArchiveMode.Create))
            for (var i = 0; i <= SiteCsvSchema.MaxArchiveEntries; i++) archive.CreateEntry($"{i}.csv");
        Check(SiteCsvSchema.ValidateUpload(tooManyEntries, true)?.Contains("too many entries") == true,
            "Excessive archive entry count must be rejected before opening CSV entries.");
        Console.WriteLine("Site CSV regressions passed: 14 required headers enforced, 7 optional headers accepted individually and together, mapping, HTTP 400 responses, and ZIP validation.");
    }
}
