// Models/NetworkLogDtos.cs
namespace SignalTracker.Models
{
    public class NetworkLogResponse
    {
        public int status { get; set; }
        public List<NetworkLogRow> data { get; set; } = new();
        public Dictionary<string, object> io_summary { get; set; } = new();
        public Dictionary<string, object> app_summary { get; set; } = new();
    }

    public class NetworkLogRow
    {
        public int id { get; set; }
        public int session_id { get; set; }
        public double? lat { get; set; }
        public double? lon { get; set; }
        public DateTime? timestamp { get; set; }
        public double? rssi { get; set; }
        public double? rsrp { get; set; }
        public double? rsrq { get; set; }
        public double? sinr { get; set; }
        public string network { get; set; } = "";
        public string band { get; set; } = "";
        public string dl_tpt { get; set; } = "";
        public string ul_tpt { get; set; } = "";
        public string provider { get; set; } = "";
        public double? jitter { get; set; }
        public double? latency { get; set; }
        public double? mos { get; set; }
        public string pci { get; set; } = "";
        public string nodeb_id { get; set; } = "";
        public string cell_id { get; set; } = "";
        public double speed { get; set; }
    }

    public class IoSummaryItem
    {
        public int inputCount { get; set; }
        public int outputCount { get; set; }
    }

    public class AppSummaryItem
    {
        public string appName { get; set; } = "";
        public int sampleCount { get; set; }
        public double avgRsrp { get; set; }
        public double avgRsrq { get; set; }
        public double avgSinr { get; set; }
        public double avgMos { get; set; }
        public double avgJitter { get; set; }
        public double avgLatency { get; set; }
        public double avgPacketLoss { get; set; }
        public double avgDlTptMbps { get; set; }
        public double avgUlTptMbps { get; set; }
        public DateTime? firstUsedAt { get; set; }
        public DateTime? lastUsedAt { get; set; }
        public int durationSeconds { get; set; }
        public string durationHHMMSS { get; set; } = "";
    }
}
