namespace SignalTracker.DTO.PythonBridge
{
    public class LtePredictionOptimisedBulkRequest
    {
        public long ProjectId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public List<LtePredictionOptimisedRow> Rows { get; set; } = new();
    }

    public class LtePredictionOptimisedRow
    {
        public double? lat { get; set; }
        public double? lon { get; set; }
        public double? pred_rsrp { get; set; }
        public double? pred_rsrq { get; set; }
        public double? pred_sinr { get; set; }
        public string? node_b_id { get; set; }
        public string? cell_id { get; set; }
        public string? operator_name { get; set; }
        public string? @operator { get; set; }
        public DateTime? created_at { get; set; }
        public string? site_id { get; set; }
        public string? nodeb_id_cell_id { get; set; }
        public string? band { get; set; }
        public string? Technology { get; set; }
        public int? scenario_id { get; set; }
        public int? public_scenario_id { get; set; }
    }
}


