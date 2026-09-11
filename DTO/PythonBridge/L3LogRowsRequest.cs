namespace SignalTracker.DTO.PythonBridge
{
    public class L3LogRowsRequest
    {
        // Required: "india"/"in" or "taiwan"/"tw". Region or CountryCode must be sent,
        // and if both are sent they must point to the same database.
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public List<long> SessionIds { get; set; } = new();
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }
}
