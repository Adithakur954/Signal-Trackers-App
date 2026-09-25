using Microsoft.EntityFrameworkCore;
using SignalTracker.DTO.PostProcessing;
using SignalTracker.Models;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SignalTracker.Services
{
    public sealed class VoiceKpiService
    {
        private readonly ApplicationDbContext _db;
        private readonly PostProcessingCapabilityService _capabilities;

        public VoiceKpiService(ApplicationDbContext db, PostProcessingCapabilityService capabilities)
        {
            _db = db;
            _capabilities = capabilities;
        }

        public async Task<VoiceKpiResponse> GetVoiceKpisAsync(PostProcessingRequest request, CancellationToken cancellationToken = default)
        {
            var sessionIds = PostProcessingCapabilityService.NormalizeSessionIds(
                (request.SessionIds ?? new List<int>())
                    .Concat(request.SessionId.HasValue ? new[] { request.SessionId.Value } : Array.Empty<int>()));

            var take = Math.Clamp(request.Take <= 0 ? 100000 : request.Take, 1, 500000);
            var capability = await _capabilities.GetCapabilitiesAsync(sessionIds, request.UploadId, take, cancellationToken);
            var response = new VoiceKpiResponse
            {
                SessionIds = sessionIds.ToList(),
                UploadId = request.UploadId,
                SourceStatus = capability
            };

            var callStats = await ReadCallSummaryStatsAsync(sessionIds, request.UploadId, cancellationToken);
            var handoverStats = await ReadHandoverStatsAsync(sessionIds, request.UploadId, take, cancellationToken);
            var mosStats = await ReadMosStatsAsync(sessionIds, cancellationToken);
            var silenceStats = await ReadSilenceStatsAsync(sessionIds, request.UploadId, take, cancellationToken);

            response.Kpis.Add(BuildCssr(callStats, capability));
            response.Kpis.Add(BuildCdr(callStats, capability));
            response.Kpis.Add(BuildCst(callStats, capability));
            response.Kpis.Add(BuildMos(mosStats, capability));
            response.Kpis.Add(BuildHsr(handoverStats, capability));
            response.Kpis.Add(BuildSilenceCount(silenceStats, capability));
            response.Kpis.Add(BuildSilenceRate(silenceStats, callStats, capability));

            return response;
        }

        private static VoiceKpiResult BuildCssr(CallSummaryStats stats, PostProcessingCapabilityResult capability)
        {
            if (stats.Attempts > 0)
            {
                var successes = stats.Connected + stats.Dropped;
                return Calculated(
                    "Call Setup Success Rate",
                    "cssr",
                    "%",
                    successes,
                    stats.Attempts,
                    "tbl_l3_event_call_summary",
                    "Successful established calls divided by total detected call attempts.",
                    RoundPercent(successes, stats.Attempts),
                    new[] { "call attempt", "call setup success" });
            }

            return MissingVoiceCallKpi(
                "Call Setup Success Rate",
                "cssr",
                "%",
                capability,
                "Voice call attempt and successful call setup events are not present in the selected data.",
                new[] { "call attempt", "call setup success" });
        }

        private static VoiceKpiResult BuildCdr(CallSummaryStats stats, PostProcessingCapabilityResult capability)
        {
            var established = stats.Connected + stats.Dropped;
            if (established > 0)
            {
                return Calculated(
                    "Call Drop Rate",
                    "cdr",
                    "%",
                    stats.Dropped,
                    established,
                    "tbl_l3_event_call_summary",
                    "Dropped established calls divided by total established calls.",
                    RoundPercent(stats.Dropped, established),
                    new[] { "established voice call", "call drop event" });
            }

            return MissingVoiceCallKpi(
                "Call Drop Rate",
                "cdr",
                "%",
                capability,
                "Established voice calls and dropped call events are not present in the selected data.",
                new[] { "established voice call", "call drop event" });
        }

        private static VoiceKpiResult BuildCst(CallSummaryStats stats, PostProcessingCapabilityResult capability)
        {
            if (stats.SetupSamples > 0 && stats.AvgSetupMs.HasValue)
            {
                return new VoiceKpiResult
                {
                    Name = "Call Setup Time",
                    Key = "cst",
                    Status = "calculated",
                    Value = Math.Round(stats.AvgSetupMs.Value / 1000d, 3),
                    Unit = "seconds",
                    Numerator = stats.SetupSamples,
                    Denominator = stats.Attempts,
                    Source = "tbl_l3_event_call_summary.setup_time",
                    Reason = "Average call setup time calculated from detected connected call setup timings.",
                    RequiredData = new List<string> { "call attempt timestamp", "call connected timestamp" }
                };
            }

            return MissingVoiceCallKpi(
                "Call Setup Time",
                "cst",
                "seconds",
                capability,
                "Call start and connected timestamps are not present in the selected data.",
                new[] { "call attempt timestamp", "call connected timestamp" });
        }

        private static VoiceKpiResult BuildMos(MosStats stats, PostProcessingCapabilityResult capability)
        {
            if (stats.Samples > 0 && stats.Average.HasValue)
            {
                return new VoiceKpiResult
                {
                    Name = "Voice Quality MOS",
                    Key = "mos",
                    Status = "calculated",
                    Value = Math.Round(stats.Average.Value, 3),
                    Unit = "MOS",
                    Numerator = stats.Samples,
                    Denominator = capability.NetworkLogRows,
                    Source = "tbl_network_log.mos",
                    Reason = "MOS calculated from non-empty network log MOS samples. If the session is not a voice test, treat this as service MOS evidence from the log.",
                    RequiredData = new List<string> { "voice MOS" }
                };
            }

            return new VoiceKpiResult
            {
                Name = "Voice Quality MOS",
                Key = "mos",
                Status = "unavailable",
                Value = null,
                Unit = "MOS",
                Source = "tbl_network_log.mos",
                Reason = "Voice MOS samples are not present in the selected log data.",
                RequiredData = new List<string> { "voice MOS" }
            };
        }

        private static VoiceKpiResult BuildHsr(HandoverStats stats, PostProcessingCapabilityResult capability)
        {
            if (stats.Attempts > 0 && (stats.Successes > 0 || stats.Failures > 0))
            {
                return Calculated(
                    "Handover Success Rate",
                    "hsr",
                    "%",
                    stats.Successes,
                    stats.Successes + stats.Failures,
                    "tbl_event_log/tbl_l3_log handover evidence",
                    "Successful handover observations divided by correlated success/failure handover observations.",
                    RoundPercent(stats.Successes, stats.Successes + stats.Failures),
                    new[] { "handover attempt", "handover success", "handover failure" });
            }

            if (capability.HasHandoverEvents || stats.Attempts > 0)
            {
                return Partial(
                    "Handover Success Rate",
                    "hsr",
                    "%",
                    "Handover observations are present, but exact handover attempt/success/failure correlation is not available.",
                    "tbl_event_log/tbl_l3_log",
                    new[] { "handover attempt", "handover success", "handover failure" },
                    stats.Attempts,
                    null);
            }

            return new VoiceKpiResult
            {
                Name = "Handover Success Rate",
                Key = "hsr",
                Status = "unavailable",
                Unit = "%",
                Source = "tbl_event_log/tbl_l3_log",
                Reason = "Handover attempt/success/failure evidence is not present in the selected data.",
                RequiredData = new List<string> { "handover attempt", "handover success", "handover failure" }
            };
        }

        private static VoiceKpiResult BuildSilenceCount(SilenceStats stats, PostProcessingCapabilityResult capability)
        {
            if (stats.EventsGreaterThan4Seconds > 0)
            {
                return Calculated(
                    "Silence Call >4s",
                    "silence_call_gt_4s",
                    "count",
                    stats.EventsGreaterThan4Seconds,
                    stats.TotalSilenceEvents,
                    "tbl_event_log silence/RTP evidence",
                    "Count of detected silence/RTP gap events longer than 4 seconds.",
                    stats.EventsGreaterThan4Seconds,
                    new[] { "RTP gap", "silence duration" });
            }

            if (capability.HasSilenceGapData || stats.TotalSilenceEvents > 0)
            {
                return Partial(
                    "Silence Call >4s",
                    "silence_call_gt_4s",
                    "count",
                    "Silence/RTP gap evidence is present, but duration greater than 4 seconds could not be confirmed.",
                    "tbl_event_log",
                    new[] { "RTP gap", "silence duration" },
                    stats.TotalSilenceEvents,
                    null);
            }

            return new VoiceKpiResult
            {
                Name = "Silence Call >4s",
                Key = "silence_call_gt_4s",
                Status = "unavailable",
                Unit = "count",
                Source = "tbl_event_log",
                Reason = "RTP gap or silence duration data is not present.",
                RequiredData = new List<string> { "RTP gap", "silence duration" }
            };
        }

        private static VoiceKpiResult BuildSilenceRate(SilenceStats silenceStats, CallSummaryStats callStats, PostProcessingCapabilityResult capability)
        {
            if (silenceStats.EventsGreaterThan4Seconds > 0 && callStats.Attempts > 0)
            {
                return Calculated(
                    "Silence Call Rate",
                    "silence_call_rate",
                    "%",
                    silenceStats.EventsGreaterThan4Seconds,
                    callStats.Attempts,
                    "tbl_event_log + tbl_l3_event_call_summary",
                    "Silence calls longer than 4 seconds divided by total detected voice call attempts.",
                    RoundPercent(silenceStats.EventsGreaterThan4Seconds, callStats.Attempts),
                    new[] { "silence call count", "total voice calls" });
            }

            if (capability.HasSilenceGapData)
            {
                return Partial(
                    "Silence Call Rate",
                    "silence_call_rate",
                    "%",
                    "Silence/RTP gap evidence is present, but total voice call denominator or >4s duration evidence is incomplete.",
                    "tbl_event_log + tbl_l3_event_call_summary",
                    new[] { "silence call count", "total voice calls" },
                    silenceStats.TotalSilenceEvents,
                    callStats.Attempts > 0 ? callStats.Attempts : null);
            }

            return new VoiceKpiResult
            {
                Name = "Silence Call Rate",
                Key = "silence_call_rate",
                Status = "unavailable",
                Unit = "%",
                Source = "tbl_event_log + tbl_l3_event_call_summary",
                Reason = "Silence call count and total voice call count are not available.",
                RequiredData = new List<string> { "silence call count", "total voice calls" }
            };
        }

        private static VoiceKpiResult MissingVoiceCallKpi(string name, string key, string unit, PostProcessingCapabilityResult capability, string unavailableReason, IEnumerable<string> requiredData)
        {
            if (capability.HasVoiceCallData)
            {
                return Partial(name, key, unit, "Voice call evidence is present, but the exact numerator/denominator needed for this KPI is incomplete.", "tbl_event_log/tbl_l3_event_call_summary", requiredData, null, null);
            }

            return new VoiceKpiResult
            {
                Name = name,
                Key = key,
                Status = "unavailable",
                Unit = unit,
                Source = "tbl_event_log/tbl_l3_event_call_summary",
                Reason = unavailableReason,
                RequiredData = requiredData.ToList()
            };
        }

        private static VoiceKpiResult Calculated(string name, string key, string unit, long numerator, long denominator, string source, string reason, double? value, IEnumerable<string> requiredData)
        {
            return new VoiceKpiResult
            {
                Name = name,
                Key = key,
                Status = "calculated",
                Value = value,
                Unit = unit,
                Numerator = numerator,
                Denominator = denominator,
                Source = source,
                Reason = reason,
                RequiredData = requiredData.ToList()
            };
        }

        private static VoiceKpiResult Partial(string name, string key, string unit, string reason, string source, IEnumerable<string> requiredData, long? numerator, long? denominator)
        {
            return new VoiceKpiResult
            {
                Name = name,
                Key = key,
                Status = "partial",
                Unit = unit,
                Numerator = numerator,
                Denominator = denominator,
                Source = source,
                Reason = reason,
                RequiredData = requiredData.ToList()
            };
        }

        private async Task<CallSummaryStats> ReadCallSummaryStatsAsync(IReadOnlyList<int> sessionIds, int? uploadId, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0 && !uploadId.HasValue)
                return new CallSummaryStats();

            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                if (!await TableExistsAsync(conn, "tbl_l3_event_call_summary", cancellationToken))
                    return new CallSummaryStats();

                await using var cmd = conn.CreateCommand();
                var where = BuildCallSummaryWhere(cmd, sessionIds, uploadId);
                cmd.CommandText = $@"
                    SELECT
                        COUNT(*) AS attempts,
                        SUM(CASE WHEN LOWER(COALESCE(call_status, '')) = 'connected' THEN 1 ELSE 0 END) AS connected,
                        SUM(CASE WHEN LOWER(COALESCE(call_status, '')) = 'dropped' THEN 1 ELSE 0 END) AS dropped,
                        SUM(CASE WHEN COALESCE(setup_time, 0) > 0 THEN 1 ELSE 0 END) AS setup_samples,
                        AVG(CASE WHEN COALESCE(setup_time, 0) > 0 THEN setup_time ELSE NULL END) AS avg_setup_ms
                    FROM tbl_l3_event_call_summary cs{where};";

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return new CallSummaryStats();

                return new CallSummaryStats
                {
                    Attempts = ReadLong(reader, 0),
                    Connected = ReadLong(reader, 1),
                    Dropped = ReadLong(reader, 2),
                    SetupSamples = ReadLong(reader, 3),
                    AvgSetupMs = ReadDouble(reader, 4)
                };
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private async Task<MosStats> ReadMosStatsAsync(IReadOnlyList<int> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return new MosStats();

            var rows = await _db.tbl_network_log
                .AsNoTracking()
                .Where(row => row.session_id.HasValue && sessionIds.Contains(row.session_id.Value) && row.mos.HasValue && row.mos.Value > 0)
                .GroupBy(_ => 1)
                .Select(group => new MosStats
                {
                    Samples = group.LongCount(),
                    Average = group.Average(row => (double?)row.mos),
                    Min = group.Min(row => (double?)row.mos),
                    Max = group.Max(row => (double?)row.mos)
                })
                .FirstOrDefaultAsync(cancellationToken);

            return rows ?? new MosStats();
        }

        private async Task<HandoverStats> ReadHandoverStatsAsync(IReadOnlyList<int> sessionIds, int? uploadId, int take, CancellationToken cancellationToken)
        {
            var texts = await ReadAllDiagnosticTextsAsync(sessionIds, uploadId, take, cancellationToken);
            var handoverRows = texts.Where(text => Regex.IsMatch(text, @"\b(HANDOVER|HO|RRC RECONFIGURATION|MOBILITY|EVENTA3|EVENTB1)\b", RegexOptions.IgnoreCase)).ToList();
            return new HandoverStats
            {
                Attempts = handoverRows.Count,
                Successes = handoverRows.Count(text => Regex.IsMatch(text, @"\b(SUCCESS|SUCCEEDED|COMPLETE|COMPLETED)\b", RegexOptions.IgnoreCase)),
                Failures = handoverRows.Count(text => Regex.IsMatch(text, @"\b(FAIL|FAILED|FAILURE|REJECT|TIMEOUT)\b", RegexOptions.IgnoreCase))
            };
        }

        private async Task<SilenceStats> ReadSilenceStatsAsync(IReadOnlyList<int> sessionIds, int? uploadId, int take, CancellationToken cancellationToken)
        {
            var texts = await ReadAllDiagnosticTextsAsync(sessionIds, uploadId, take, cancellationToken);
            var silenceRows = texts.Where(text => Regex.IsMatch(text, @"\b(SILENCE|RTP GAP|NO RTP|AUDIO GAP|VOICE GAP)\b", RegexOptions.IgnoreCase)).ToList();
            return new SilenceStats
            {
                TotalSilenceEvents = silenceRows.Count,
                EventsGreaterThan4Seconds = silenceRows.Count(HasDurationGreaterThanFourSeconds)
            };
        }

        private async Task<List<string>> ReadAllDiagnosticTextsAsync(IReadOnlyList<int> sessionIds, int? uploadId, int take, CancellationToken cancellationToken)
        {
            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                var eventRows = await PostProcessingCapabilityService.ReadDiagnosticTextRowsAsync(conn, "tbl_event_log", sessionIds, uploadId, take, cancellationToken);
                var l3Rows = await PostProcessingCapabilityService.ReadDiagnosticTextRowsAsync(conn, "tbl_l3_log", sessionIds, uploadId, take, cancellationToken);
                eventRows.AddRange(l3Rows);
                return eventRows;
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private static bool HasDurationGreaterThanFourSeconds(string text)
        {
            foreach (Match match in Regex.Matches(text, @"(?<num>\d+(?:\.\d+)?)\s*(?<unit>ms|msec|milliseconds?|s|sec|seconds?)\b", RegexOptions.IgnoreCase))
            {
                var number = double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture);
                var unit = match.Groups["unit"].Value.ToLowerInvariant();
                var seconds = unit.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? number / 1000d : number;
                if (seconds > 4d)
                    return true;
            }

            return Regex.IsMatch(text, @">\s*4\s*(s|sec|seconds?)\b", RegexOptions.IgnoreCase);
        }

        private static double? RoundPercent(long numerator, long denominator)
        {
            if (denominator <= 0)
                return null;
            return Math.Round(numerator * 100d / denominator, 3);
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

        private static long ReadLong(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static double? ReadDouble(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private sealed class CallSummaryStats
        {
            public long Attempts { get; init; }
            public long Connected { get; init; }
            public long Dropped { get; init; }
            public long SetupSamples { get; init; }
            public double? AvgSetupMs { get; init; }
        }

        private sealed class MosStats
        {
            public long Samples { get; init; }
            public double? Average { get; init; }
            public double? Min { get; init; }
            public double? Max { get; init; }
        }

        private sealed class HandoverStats
        {
            public long Attempts { get; init; }
            public long Successes { get; init; }
            public long Failures { get; init; }
        }

        private sealed class SilenceStats
        {
            public long TotalSilenceEvents { get; init; }
            public long EventsGreaterThan4Seconds { get; init; }
        }
    }
}

