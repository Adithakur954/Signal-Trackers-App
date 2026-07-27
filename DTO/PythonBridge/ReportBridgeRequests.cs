namespace SignalTracker.DTO.PythonBridge
{
    public class SessionIdsPagedRequest
    {
        public List<long> SessionIds { get; set; } = new();
        public long? ProjectId { get; set; }
        public string? Provider { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; }
    }

    public class ProjectDownloadPathUpdateRequest
    {
        public long ProjectId { get; set; }
        public string DownloadPath { get; set; } = string.Empty;
    }
}


