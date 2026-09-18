using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;

namespace SignalTracker.Helper;

public static class SiteCsvSchema
{
    // Preserve the required columns from the original site-upload contract.
    // The template also includes optional columns; their absence must not reject an upload.
    private static readonly string[] RequiredHeaders =
    [
        "site", "sector", "cell_id", "longitude", "latitude", "pci", "azimuth",
        "band", "earfcn", "cluster", "Technology", "m_tilt", "e_tilt", "height"
    ];

    public static string NormalizeHeader(string header) => header.Trim().Trim('\uFEFF').Trim().ToLowerInvariant();

    public static string? ValidateHeader(string? headerLine, out string[] headers)
    {
        headers = [];
        if (string.IsNullOrWhiteSpace(headerLine))
            return "Site data could not be uploaded. The CSV header is empty. Use Site_Template.csv.";

        try
        {
            using var reader = new StringReader(headerLine);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
            if (!csv.Read() || !csv.ReadHeader())
                return "Site data could not be uploaded. The CSV header is empty. Use Site_Template.csv.";
            headers = (csv.HeaderRecord ?? []).Select(NormalizeHeader).ToArray();
        }
        catch (CsvHelperException)
        {
            return "Site data could not be uploaded. The CSV header is invalid. Use Site_Template.csv.";
        }

        var actual = headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredHeaders.Where(header => !actual.Contains(header)).ToArray();
        if (missing.Length > 0)
            return "Site data could not be uploaded. Missing required columns: "
                + string.Join(", ", missing) + ". Use Site_Template.csv.";

        var duplicate = headers.GroupBy(header => header).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicate.Length > 0)
            return "Site data could not be uploaded. Duplicate CSV columns: " + string.Join(", ", duplicate) + ".";

        return null;
    }

    public static string? ValidateFile(string path)
    {
        using var reader = new StreamReader(path);
        return ValidateHeader(reader.ReadLine(), out _);
    }

    public static string? ValidateUpload(string path, bool isZip)
    {
        if (!isZip) return ValidateFile(path);

        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0)
            return "Site data could not be uploaded. The ZIP does not contain any CSV files.";

        var errors = new List<string>();
        foreach (var entry in entries)
        {
            // Match the importer's per-entry bound before decompressing anything.
            if (entry.Length > 200L * 1024 * 1024)
                return "Site data could not be uploaded. A CSV in the ZIP exceeds the 200 MB limit.";
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var error = ValidateHeader(reader.ReadLine(), out _);
            if (error != null) errors.Add($"{entry.Name}: {error}");
        }
        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }
}
