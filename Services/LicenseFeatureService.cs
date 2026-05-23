using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services
{
    public class LicenseFeatureService
    {
        public const string FeatureReportGeneration = "report_generation";
        public const string FeatureBenchmarkTab = "benchmark_tab";
        public const string FeatureRunPrediction = "run_prediction";
        public const string FeatureGridFetch = "grid_fetch";
        private const string SuperAdminCountryCode = "TW";

        private static readonly string[] DefaultFeatures =
        {
            FeatureReportGeneration,
            FeatureBenchmarkTab,
            FeatureRunPrediction,
            FeatureGridFetch
        };

        private static readonly Dictionary<string, string> FeatureAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["report"] = FeatureReportGeneration,
            ["report_generation"] = FeatureReportGeneration,
            ["reportgeneration"] = FeatureReportGeneration,
            ["generate_report"] = FeatureReportGeneration,
            ["generate_pdf"] = FeatureReportGeneration,
            ["pdf_report"] = FeatureReportGeneration,

            ["benchmark"] = FeatureBenchmarkTab,
            ["benchmark_tab"] = FeatureBenchmarkTab,
            ["operatorcomparison"] = FeatureBenchmarkTab,
            ["operator_comparison"] = FeatureBenchmarkTab,

            ["run_prediction"] = FeatureRunPrediction,
            ["runprediction"] = FeatureRunPrediction,
            ["prediction"] = FeatureRunPrediction,
            ["lte_prediction"] = FeatureRunPrediction,
            ["run_lte_prediction"] = FeatureRunPrediction,

            ["grid_fetch"] = FeatureGridFetch,
            ["gridfetch"] = FeatureGridFetch,
            ["fetch_grid"] = FeatureGridFetch,
            ["fetchgrid"] = FeatureGridFetch,
            ["grid_api"] = FeatureGridFetch,
            ["grid_compute"] = FeatureGridFetch,
            ["compute_grid"] = FeatureGridFetch,
        };

        private readonly ApplicationDbContext _db;

        public LicenseFeatureService(ApplicationDbContext db)
        {
            _db = db;
        }

        public static IReadOnlyList<string> NormalizeFeatures(IEnumerable<string>? values)
        {
            if (values == null) return Array.Empty<string>();

            var normalized = values
                .SelectMany(v => SplitRaw(v))
                .Select(Canonicalize)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return normalized;
        }

        public static List<string>? ExtractFeaturesFromRequest(
            IEnumerable<string>? features = null,
            IEnumerable<string>? featureList = null,
            IEnumerable<string>? enabledFeatures = null,
            IEnumerable<string>? permissions = null,
            string? featureCodes = null,
            string? featuresCsv = null)
        {
            var raw = new List<string>();

            if (features != null) raw.AddRange(features);
            if (featureList != null) raw.AddRange(featureList);
            if (enabledFeatures != null) raw.AddRange(enabledFeatures);
            if (permissions != null) raw.AddRange(permissions);
            if (!string.IsNullOrWhiteSpace(featureCodes)) raw.Add(featureCodes);
            if (!string.IsNullOrWhiteSpace(featuresCsv)) raw.Add(featuresCsv);

            if (raw.Count == 0)
                return null;

            return NormalizeFeatures(raw).ToList();
        }

        public async Task UpsertLicenseFeaturesAsync(int licenseId, IReadOnlyList<string> features, CancellationToken ct = default)
        {
            var conn = _db.Database.GetDbConnection();
            var shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                shouldClose = true;
            }

            try
            {
                await EnsureTableAsync(conn, ct);
                var csv = string.Join(",", features ?? Array.Empty<string>());

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO license_feature_access (license_id, feature_codes, updated_at)
VALUES (@license_id, @feature_codes, UTC_TIMESTAMP())
ON DUPLICATE KEY UPDATE
    feature_codes = VALUES(feature_codes),
    updated_at = UTC_TIMESTAMP();";
                AddParam(cmd, "@license_id", DbType.Int32, licenseId);
                AddParam(cmd, "@feature_codes", DbType.String, csv);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        public async Task<Dictionary<int, List<string>>> GetFeaturesForLicenseIdsAsync(IEnumerable<int> licenseIds, CancellationToken ct = default)
        {
            var ids = (licenseIds ?? Array.Empty<int>()).Distinct().Where(x => x > 0).ToList();
            var map = new Dictionary<int, List<string>>();
            if (ids.Count == 0) return map;

            var conn = _db.Database.GetDbConnection();
            var shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                shouldClose = true;
            }

            try
            {
                await EnsureTableAsync(conn, ct);
                var placeholders = new List<string>();
                await using var cmd = conn.CreateCommand();
                for (var i = 0; i < ids.Count; i++)
                {
                    var pName = $"@id{i}";
                    placeholders.Add(pName);
                    AddParam(cmd, pName, DbType.Int32, ids[i]);
                }

                cmd.CommandText = $@"
SELECT license_id, feature_codes
FROM license_feature_access
WHERE license_id IN ({string.Join(", ", placeholders)});";

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var licenseId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                    var raw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    map[licenseId] = NormalizeFeatures(new[] { raw }).ToList();
                }
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }

            return map;
        }

        public async Task<List<string>> GetEnabledFeaturesForUserAsync(int userId, CancellationToken ct = default)
        {
            var snapshot = await GetFeatureAccessSnapshotAsync(userId, ct);
            return snapshot.EnabledFeatures;
        }

        public async Task<bool> HasFeatureAccessAsync(int userId, string feature, bool defaultAllow = false, CancellationToken ct = default)
        {
            var snapshot = await GetFeatureAccessSnapshotAsync(userId, ct);
            if (snapshot.IsSuperAdmin)
                return true;

            if (snapshot.EnabledFeatures.Count == 0)
                return defaultAllow;

            var normalizedFeature = Canonicalize(feature);
            return snapshot.EnabledFeatures.Any(x => string.Equals(x, normalizedFeature, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<FeatureAccessSnapshot> GetFeatureAccessSnapshotAsync(int userId, CancellationToken ct = default)
        {
            if (userId <= 0)
                return new FeatureAccessSnapshot(false, new List<string>());

            var conn = _db.Database.GetDbConnection();
            var shouldClose = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                shouldClose = true;
            }

            try
            {
                await EnsureTableAsync(conn, ct);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT u.m_user_type_id, u.country_code, lfa.license_id, lfa.feature_codes
FROM tbl_user u
LEFT JOIN tbl_company_user_license_issued lic ON lic.tbl_user_id = u.id
    AND lic.status = 1
    AND DATE(lic.valid_till) >= UTC_DATE()
LEFT JOIN license_feature_access lfa ON lfa.license_id = lic.id
WHERE u.id = @uid
ORDER BY lic.valid_till DESC, lic.created_on DESC, lic.id DESC
LIMIT 1;";
                AddParam(cmd, "@uid", DbType.Int32, userId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return new FeatureAccessSnapshot(false, new List<string>());

                var userTypeId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                var countryCode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (IsSuperAdminUser(userTypeId, countryCode))
                    return new FeatureAccessSnapshot(true, DefaultFeatures.ToList());

                var hasFeatureConfig = !reader.IsDBNull(2);
                if (!hasFeatureConfig)
                    return new FeatureAccessSnapshot(false, new List<string>());

                var raw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                return new FeatureAccessSnapshot(false, NormalizeFeatures(new[] { raw }).ToList());
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private static bool IsSuperAdminUser(int userTypeId, string? countryCode)
        {
            return userTypeId == UserScopeService.ROLE_SUPER_ADMIN
                || string.Equals(countryCode, SuperAdminCountryCode, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitRaw(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            var text = raw.Trim();

            if ((text.StartsWith("[") && text.EndsWith("]")) || (text.StartsWith("{") && text.EndsWith("}")))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return doc.RootElement.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString() ?? string.Empty)
                            .ToArray();
                    }
                }
                catch
                {
                    // fallback to separator parsing
                }
            }

            return text.Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim());
        }

        private static string Canonicalize(string? value)
        {
            var normalized = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");

            if (FeatureAliases.TryGetValue(normalized, out var canonical))
                return canonical;

            return normalized;
        }

        private static void AddParam(DbCommand cmd, string name, DbType type, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.DbType = type;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static async Task EnsureTableAsync(DbConnection conn, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS license_feature_access (
    id INT AUTO_INCREMENT PRIMARY KEY,
    license_id INT NOT NULL,
    feature_codes TEXT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_license_feature_access_license (license_id),
    INDEX idx_license_feature_access_license (license_id)
);";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private sealed class FeatureAccessSnapshot
        {
            public FeatureAccessSnapshot(bool isSuperAdmin, List<string> enabledFeatures)
            {
                IsSuperAdmin = isSuperAdmin;
                EnabledFeatures = enabledFeatures;
            }

            public bool IsSuperAdmin { get; }
            public List<string> EnabledFeatures { get; }
        }
    }
}


