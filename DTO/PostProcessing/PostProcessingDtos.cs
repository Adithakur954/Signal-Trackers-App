namespace SignalTracker.DTO.PostProcessing
{
    public sealed class PostProcessingRequest
    {
        public int? SessionId { get; set; }
        public List<int>? SessionIds { get; set; }
        public int? UploadId { get; set; }
        public int Take { get; set; } = 100000;
    }

    public sealed class PostProcessingCapabilityResult
    {
        public List<int> SessionIds { get; set; } = new();
        public int? UploadId { get; set; }
        public bool HasNetworkLog { get; set; }
        public bool HasL3 { get; set; }
        public bool HasEvent { get; set; }
        public bool HasSubSessions { get; set; }
        public bool HasCallSummary { get; set; }
        public bool HasVoiceCallData { get; set; }
        public bool HasMosSamples { get; set; }
        public bool HasHandoverEvents { get; set; }
        public bool HasSilenceGapData { get; set; }
        public bool HasTechnologyData { get; set; }
        public bool HasLocationData { get; set; }
        public long NetworkLogRows { get; set; }
        public long L3Rows { get; set; }
        public long EventRows { get; set; }
        public long SubSessionRows { get; set; }
        public long CallSummaryRows { get; set; }
        public Dictionary<string, long> Technologies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Notes { get; set; } = new();
    }

    public sealed class VoiceKpiResponse
    {
        public int Status { get; set; } = 1;
        public string Message { get; set; } = "OK";
        public List<int> SessionIds { get; set; } = new();
        public int? UploadId { get; set; }
        public PostProcessingCapabilityResult SourceStatus { get; set; } = new();
        public List<VoiceKpiResult> Kpis { get; set; } = new();
    }

    public sealed class VoiceKpiResult
    {
        public string Name { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Status { get; set; } = "unavailable";
        public double? Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public long? Numerator { get; set; }
        public long? Denominator { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<string> RequiredData { get; set; } = new();
    }
}
