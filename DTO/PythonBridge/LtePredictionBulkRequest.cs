namespace SignalTracker.DTO.PythonBridge
{
    public class LtePredictionBulkRequest
    {
        public long ProjectId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public List<LtePredictionRow> Rows { get; set; } = new();
    }

    public class LtePredictionRow
    {
        public double? lat { get; set; }
        public double? lon { get; set; }
        public double? pred_rsrp { get; set; }
        public double? pred_rsrq { get; set; }
        public double? pred_sinr { get; set; }
        public string? site_id { get; set; }
    }
}


