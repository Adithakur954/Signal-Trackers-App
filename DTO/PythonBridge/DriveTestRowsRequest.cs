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
        // Optional technology filter -- exact `network` column values to keep (e.g. "4G",
        // "4G (LTE Anchor - NSA)", "5G NSA"). Null/empty = no filter, every technology kept
        // (unchanged default behavior for existing callers). The caller is responsible for
        // resolving a generation picker ("2G"/"3G"/"4G"/"5G") into the exact literal `network`
        // values present for that project before setting this -- this field does exact
        // matching only, it does not do generation-bucket pattern matching itself.
        public List<string>? Technologies { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }
}


