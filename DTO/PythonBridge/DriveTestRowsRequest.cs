namespace SignalTracker.DTO.PythonBridge
{
    public class DriveTestRowsRequest
    {
        public long? ProjectId { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public List<long> SessionIds { get; set; } = new();
        public bool IncludeNeighbour { get; set; } = true;
        public string? Operator { get; set; }
        public bool PrimaryOnly { get; set; } = false;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }
}


