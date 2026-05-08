namespace SignalTracker.DTO.PythonBridge
{
    public class RfOptimizationBulkRequest
    {
        public long ProjectId { get; set; }
        public int ScenarioId { get; set; }
        public List<RfOptimizationRow> Rows { get; set; } = new();
    }

    public class RfOptimizationRow
    {
        public string? @operator { get; set; }
        public string? cell_id { get; set; }
        public string? technology { get; set; }
        public string? parameter { get; set; }
        public string? current_value { get; set; }
        public string? recommended_value { get; set; }
        public string? reason { get; set; }
        public string? swap_sector_detected { get; set; }
        public double? rsrp_threshold { get; set; }
        public double? rsrq_threshold { get; set; }
        public double? sinr_threshold { get; set; }
        public DateTime? created_at { get; set; }
    }
}
