namespace SignalTracker.DTO.PythonBridge
{
    public class DriveTestRowsRequest
    {
        public List<long> SessionIds { get; set; } = new();
        public bool IncludeNeighbour { get; set; } = true;
        public string? Operator { get; set; }
        public bool PrimaryOnly { get; set; } = false;
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }
}
