namespace SignalTracker.DTO.PythonBridge
{
    public class PredictionDataBulkRequest
    {
        public long ProjectId { get; set; }
        public bool ReplaceProjectData { get; set; }
        public List<PredictionDataRow> Rows { get; set; } = new();
    }

    public class PredictionDataRow
    {
        public double? lat { get; set; }
        public double? lon { get; set; }
        public double? rsrp { get; set; }
        public double? rsrq { get; set; }
        public double? sinr { get; set; }
        public string? serving_cell { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
        public string? pci { get; set; }
        public string? network { get; set; }
        public string? azimuth { get; set; }
        public string? tx_power { get; set; }
        public string? height { get; set; }
        public string? reference_signal_power { get; set; }
        public string? mtilt { get; set; }
        public string? etilt { get; set; }
    }
}


