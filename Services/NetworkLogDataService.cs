using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SignalTracker.Services
{
    public sealed class NetworkLogDataService
    {
        public const int DefaultMapCacheTtlSeconds = 300;

        public IReadOnlyList<long> ParseSessionIds(params string?[] rawValues)
        {
            var raw = rawValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<long>();

            return raw
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        public string BuildSessionIdsCachePart(IEnumerable<long> sessionIds)
        {
            var joined = string.Join("-", sessionIds.OrderBy(x => x));
            if (string.IsNullOrWhiteSpace(joined))
                return "all";

            if (joined.Length <= 160)
                return joined;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))
                .ToLowerInvariant();
            return $"{joined.Count(c => c == '-') + 1}-{hash[..20]}";
        }

        public string? NormalizeProvider(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var value = raw.Trim().ToLowerInvariant();
            if (value.StartsWith("j", StringComparison.Ordinal)) return "jio";
            if (value.StartsWith("a", StringComparison.Ordinal)) return "airtel";
            if (value.StartsWith("v", StringComparison.Ordinal)) return "vodafone";
            if (value.StartsWith("b", StringComparison.Ordinal)) return "bsnl";
            return value;
        }

        public object ToInclusiveFrom(DateTime? startDate)
            => startDate.HasValue ? startDate.Value : DBNull.Value;

        public object ToExclusiveTo(DateTime? endDate)
            => endDate.HasValue ? endDate.Value.AddDays(1) : DBNull.Value;

        public object ToProviderLikeParameter(string? provider)
            => string.IsNullOrWhiteSpace(provider) ? DBNull.Value : $"%{provider}%";

        public (string Clause, Dictionary<string, object> Params) BuildNetworkLogSqlWhere(
            IEnumerable<long> sessionIds,
            string? provider,
            string? networkType,
            DateTime? startDate,
            DateTime? endDate)
        {
            var parameters = new Dictionary<string, object>();
            var ids = sessionIds.Distinct().ToList();

            var idParams = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var parameterName = $"@sid{i}";
                idParams.Add(parameterName);
                parameters.Add(parameterName, ids[i]);
            }

            var clauses = new List<string>
            {
                idParams.Count > 0 ? $"session_id IN ({string.Join(",", idParams)})" : "1 = 0",
                "UPPER(TRIM(COALESCE(band, ''))) <> 'UNKNOWN'",
                "primary_cell_info_1 IS NOT NULL AND TRIM(primary_cell_info_1) <> ''",
                @"(
        NULLIF(TRIM(band), '') IS NOT NULL
        OR UPPER(TRIM(COALESCE(network, ''))) LIKE '%5G%'
    )"
            };

            if (!string.IsNullOrWhiteSpace(provider))
            {
                clauses.Add("COALESCE(NULLIF(TRIM(m_alpha_short), ''), m_alpha_long) LIKE @prov");
                parameters.Add("@prov", $"%{provider}%");
            }

            const string wifiPredicate = @"(
        primary_cell_info_1 LIKE 'SSID:%'
        OR primary_cell_info_1 LIKE '%BSSID:%'
        OR EXISTS (
            SELECT 1
            FROM tbl_session s
            WHERE s.id = session_id
              AND LOWER(COALESCE(s.type, '')) = 'wifi'
        )
    )";
            const string registeredCellPredicate = "primary_cell_info_1 LIKE '%mRegistered=YES%'";
            const string fiveGCellPredicate = @"(
        UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%5G%'
        OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%NRARFCN%'
        OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%MNR%'
        OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%NCI%'
        OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) REGEXP '(^|[^A-Z0-9])NR([^A-Z0-9]|$)'
        OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) REGEXP '(^|[^A-Z0-9])N[0-9]{1,3}([^A-Z0-9]|$)'
    )";

            if (!string.IsNullOrWhiteSpace(networkType) &&
                !networkType.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                var trimmedNetworkType = networkType.Trim();
                if (trimmedNetworkType.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
                    trimmedNetworkType.Equals("wi-fi", StringComparison.OrdinalIgnoreCase))
                {
                    clauses.Add(wifiPredicate);
                }
                else if (trimmedNetworkType.Equals("5g", StringComparison.OrdinalIgnoreCase) ||
                         trimmedNetworkType.Equals("5g nsa", StringComparison.OrdinalIgnoreCase) ||
                         trimmedNetworkType.Equals("nr", StringComparison.OrdinalIgnoreCase))
                {
                    clauses.Add(fiveGCellPredicate);
                }
                else
                {
                    clauses.Add("network IS NOT NULL AND network LIKE @networkType");
                    parameters.Add("@networkType", $"%{trimmedNetworkType}%");
                    clauses.Add(registeredCellPredicate);
                }
            }
            else
            {
                clauses.Add($"({registeredCellPredicate} OR {wifiPredicate} OR {fiveGCellPredicate})");
            }

            if (startDate.HasValue)
            {
                clauses.Add("timestamp >= @from");
                parameters.Add("@from", startDate.Value);
            }

            if (endDate.HasValue)
            {
                clauses.Add("timestamp < @to");
                parameters.Add("@to", endDate.Value.AddDays(1));
            }

            return (string.Join(" AND ", clauses), parameters);
        }

        public string BuildSessionIdPlaceholders(IEnumerable<long> sessionIds, string prefix = "@sid")
            => string.Join(",", sessionIds.Select((_, i) => $"{prefix}{i}"));

        public void AddSessionIdParameters(DbCommand command, IEnumerable<long> sessionIds, string prefix = "@sid")
        {
            var i = 0;
            foreach (var sessionId in sessionIds)
            {
                Add(command, $"{prefix}{i++}", sessionId);
            }
        }

        public void Add(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
