using Microsoft.EntityFrameworkCore;
using SignalTracker.DTO.PostProcessing;
using SignalTracker.Models;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SignalTracker.Services
{
    public sealed class PostProcessingCapabilityService
    {
        private readonly ApplicationDbContext _db;

        public PostProcessingCapabilityService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<PostProcessingCapabilityResult> GetCapabilitiesAsync(
            IReadOnlyList<int> sessionIds,
            int? uploadId,
            int take = 100000,
            CancellationToken cancellationToken = default)
        {
            var ids = NormalizeSessionIds(sessionIds);
            var result = new PostProcessingCapabilityResult
            {
                SessionIds = ids.ToList(),
                UploadId = uploadId
            };

            if (ids.Count == 0 && !uploadId.HasValue)
            {
                result.Notes.Add("No sessionId, sessionIds, or uploadId was provided.");
                return result;
            }

            result.NetworkLogRows = await CountNetworkRowsAsync(ids, cancellationToken);
            result.HasNetworkLog = result.NetworkLogRows > 0;
            result.HasMosSamples = await HasNetworkMosSamplesAsync(ids, cancellationToken);
            result.HasTechnologyData = await HasTechnologyDataAsync(ids, cancellationToken);
            result.HasLocationData = await HasLocationDataAsync(ids, cancellationToken);
            result.Technologies = await GetTechnologyCountsAsync(ids, cancellationToken);
            result.SubSessionRows = await CountSubSessionsAsync(ids, cancellationToken);
            result.HasSubSessions = result.SubSessionRows > 0;

            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                result.L3Rows = await CountDiagnosticRowsAsync(conn, "tbl_l3_log", ids, uploadId, take, cancellationToken);
                result.EventRows = await CountDiagnosticRowsAsync(conn, "tbl_event_log", ids, uploadId, take, cancellationToken);
                result.CallSummaryRows = await CountCallSummaryRowsAsync(conn, ids, uploadId, cancellationToken);
                result.HasL3 = result.L3Rows > 0;
                result.HasEvent = result.EventRows > 0;
                result.HasCallSummary = result.CallSummaryRows > 0;
                result.HasVoiceCallData = result.HasCallSummary || await HasDiagnosticTextAsync(
                    conn,
                    "tbl_event_log",
                    ids,
                    uploadId,
                    take,
                    new[] { "CALL_DIAL", "CALL_ACTIVE", "CALL_DISCONNECTED", "SIP INVITE", "SIP/2.0 200 OK", "MO_CALL", "MT_CALL", "CALL_STATE", "mPreciseCallState" },
                    cancellationToken);
                result.HasHandoverEvents = await HasDiagnosticTextAsync(
                    conn,
                    "tbl_event_log",
                    ids,
                    uploadId,
                    take,
                    new[] { "HANDOVER", "HO ", "A3", "A4", "A5", "B1", "B2", "RRC RECONFIGURATION", "MOBILITY" },
                    cancellationToken) || await HasDiagnosticTextAsync(
                    conn,
                    "tbl_l3_log",
                    ids,
                    uploadId,
                    take,
                    new[] { "HANDOVER", "RRC RECONFIGURATION", "MOBILITY", "EVENTA3", "EVENTB1" },
                    cancellationToken);
                result.HasSilenceGapData = await HasDiagnosticTextAsync(
                    conn,
                    "tbl_event_log",
                    ids,
                    uploadId,
                    take,
                    new[] { "SILENCE", "RTP GAP", "NO RTP", "AUDIO GAP", "VOICE GAP" },
                    cancellationToken);
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }

            if (!result.HasVoiceCallData)
                result.Notes.Add("Voice call setup/drop evidence was not found in the selected data.");
            if (!result.HasMosSamples)
                result.Notes.Add("Voice MOS samples were not found in tbl_network_log for the selected session(s).");
            if (!result.HasSilenceGapData)
                result.Notes.Add("Silence/RTP gap evidence was not found in the selected diagnostic data.");

            return result;
        }

        public static IReadOnlyList<int> NormalizeSessionIds(IEnumerable<int>? sessionIds)
        {
            return (sessionIds ?? Array.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .Take(250)
                .ToList();
        }

        private async Task<long> CountNetworkRowsAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return 0;

            return await _db.tbl_network_log
                .AsNoTracking()
                .Where(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value))
                .LongCountAsync(cancellationToken);
        }

        private async Task<long> CountSubSessionsAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return 0;

            return await _db.tbl_sub_session
                .AsNoTracking()
                .Where(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value))
                .LongCountAsync(cancellationToken);
        }

        private async Task<bool> HasNetworkMosSamplesAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return false;

            return await _db.tbl_network_log
                .AsNoTracking()
                .AnyAsync(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value) && row.mos.HasValue && row.mos.Value > 0, cancellationToken);
        }

        private async Task<bool> HasTechnologyDataAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return false;

            return await _db.tbl_network_log
                .AsNoTracking()
                .AnyAsync(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value) && row.network != null && row.network != "", cancellationToken);
        }

        private async Task<bool> HasLocationDataAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return false;

            return await _db.tbl_network_log
                .AsNoTracking()
                .AnyAsync(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value) && row.lat.HasValue && row.lon.HasValue, cancellationToken);
        }

        private async Task<Dictionary<string, long>> GetTechnologyCountsAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            var rows = await _db.tbl_network_log
                .AsNoTracking()
                .Where(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value) && row.network != null && row.network != "")
                .GroupBy(row => row.network!)
                .Select(group => new { Technology = group.Key, Count = group.LongCount() })
                .OrderByDescending(row => row.Count)
                .Take(25)
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(row => row.Technology, row => row.Count, StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<long> CountDiagnosticRowsAsync(DbConnection conn, string tableName, IReadOnlyList<int> sessionIds, int? uploadId, int take, CancellationToken cancellationToken)
        {
            if (!await TableExistsAsync(conn, tableName, cancellationToken))
                return 0;

            await using var cmd = conn.CreateCommand();
            var where = BuildDiagnosticWhere(cmd, sessionIds, uploadId);
            AddParam(cmd, "@take", Math.Clamp(take, 1, 500000));
            cmd.CommandText = $"SELECT COUNT(*) FROM (SELECT 1 FROM `{tableName}`{where} LIMIT @take) scoped;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
        }

        private static async Task<long> CountCallSummaryRowsAsync(DbConnection conn, IReadOnlyList<int> sessionIds, int? uploadId, CancellationToken cancellationToken)
        {
            if (!await TableExistsAsync(conn, "tbl_l3_event_call_summary", cancellationToken))
                return 0;

            await using var cmd = conn.CreateCommand();
            var where = BuildCallSummaryWhere(cmd, sessionIds, uploadId);
            cmd.CommandText = $"SELECT COUNT(*) FROM tbl_l3_event_call_summary cs{where};";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
        }

        private static async Task<bool> HasDiagnosticTextAsync(DbConnection conn, string tableName, IReadOnlyList<int> sessionIds, int? uploadId, int take, IReadOnlyList<string> tokens, CancellationToken cancellationToken)
        {
            if (!await TableExistsAsync(conn, tableName, cancellationToken))
                return false;

            var rows = await ReadDiagnosticTextRowsAsync(conn, tableName, sessionIds, uploadId, take, cancellationToken);
            return rows.Any(text => tokens.Any(token => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static async Task<List<string>> ReadDiagnosticTextRowsAsync(DbConnection conn, string tableName, IReadOnlyList<int> sessionIds, int? uploadId, int take, CancellationToken cancellationToken)
        {
            var rows = new List<string>();
            if (!await TableExistsAsync(conn, tableName, cancellationToken))
                return rows;

            var columns = tableName.Equals("tbl_l3_log", StringComparison.OrdinalIgnoreCase)
                ? "category, message, detail, cause, raw_text, source, severity"
                : "category, event_name, detail, cause, source, severity";

            await using var cmd = conn.CreateCommand();
            var where = BuildDiagnosticWhere(cmd, sessionIds, uploadId);
            AddParam(cmd, "@take", Math.Clamp(take, 1, 500000));
            cmd.CommandText = $"SELECT {columns} FROM `{tableName}`{where} ORDER BY session_id, id LIMIT @take;";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var parts = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (!reader.IsDBNull(i))
                        parts.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty);
                }
                rows.Add(string.Join(" ", parts));
            }

            return rows;
        }

        private static async Task<bool> TableExistsAsync(DbConnection conn, string tableName, CancellationToken cancellationToken)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name = @tableName
                LIMIT 1;";
            AddParam(cmd, "@tableName", tableName);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            return value != null && value != DBNull.Value;
        }

        private static string BuildDiagnosticWhere(DbCommand cmd, IReadOnlyList<int> sessionIds, int? uploadId)
        {
            var clauses = new List<string>();
            if (sessionIds.Count > 0)
            {
                var names = new List<string>();
                for (var i = 0; i < sessionIds.Count; i++)
                {
                    var name = "@sid" + i.ToString(CultureInfo.InvariantCulture);
                    names.Add(name);
                    AddParam(cmd, name, sessionIds[i]);
                }
                clauses.Add($"session_id IN ({string.Join(",", names)})");
            }

            if (uploadId.HasValue && uploadId.Value > 0)
            {
                AddParam(cmd, "@uploadId", uploadId.Value);
                clauses.Add("tbl_upload_id = @uploadId");
            }

            return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
        }

        private static string BuildCallSummaryWhere(DbCommand cmd, IReadOnlyList<int> sessionIds, int? uploadId)
        {
            var clauses = new List<string>();
            if (sessionIds.Count > 0)
            {
                var names = new List<string>();
                for (var i = 0; i < sessionIds.Count; i++)
                {
                    var name = "@sid" + i.ToString(CultureInfo.InvariantCulture);
                    names.Add(name);
                    AddParam(cmd, name, sessionIds[i]);
                }
                clauses.Add($"cs.session_id IN ({string.Join(",", names)})");
            }

            if (uploadId.HasValue && uploadId.Value > 0)
            {
                AddParam(cmd, "@uploadId", uploadId.Value);
                clauses.Add(@"EXISTS (
                    SELECT 1
                    FROM tbl_l3_event_history h
                    WHERE h.id = cs.tbl_l3_event_history_id
                      AND h.tbl_upload_id = @uploadId
                )");
            }

            return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}



