namespace SignalTracker.DTO.SitePrediction
{
    public class SitePredictionDeleteResult
    {
        public int Status { get; set; } = 1;
        public string Message { get; set; } = "";
        public int RowsAffected { get; set; }
        public int DeletedSourceRows { get; set; }
        public int DeletedOptimizedRows { get; set; }
        public bool OptimizedOnly { get; set; }
        public IReadOnlyList<long> DeletedSourceIds { get; set; } = Array.Empty<long>();
        public long RequestedProjectId { get; set; }
        public long? RequestedSourceId { get; set; }
        public string? RequestedCellId { get; set; }
        public string? RequestedSite { get; set; }
        public string? RequestedSector { get; set; }
        public bool RequestedDeleteEntireSite { get; set; }
    }
}
