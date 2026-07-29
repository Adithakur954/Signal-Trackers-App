namespace SignalTracker.DTO.PythonBridge
{
    public class LteTiltBaselineRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public string? Operator { get; set; }
        public int Limit { get; set; } = 5000;
        public int Offset { get; set; } = 0;
    }

    public class LteTiltAntennaRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public int Limit { get; set; } = 5000;
        public int Offset { get; set; } = 0;
    }

    public class LtePredictionGeoFeatureRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public int Limit { get; set; } = 5000;
        public int Offset { get; set; } = 0;
    }
}


