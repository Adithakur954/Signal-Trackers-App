namespace SignalTracker.DTO.PythonBridge
{
    public class LtePredictionRefinedBulkRequest
    {
        public long ProjectId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public List<LtePredictionRefinedRow> Rows { get; set; } = new();
    }

    public class LtePredictionRefinedRow
    {
        public double? lat { get; set; }
        public double? lon { get; set; }
        public string? site_id { get; set; }
        public double? pred_rsrp_top2_avg { get; set; }
        public double? pred_rsrp_top3_avg { get; set; }
        public double? measured_dt_rsrp { get; set; }
    }
}


