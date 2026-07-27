namespace SignalTracker.DTO.SitePrediction
{
    public class DeleteSitePredictionRequest
    {
        public long ProjectId { get; set; }
        public long? SourceId { get; set; }
        public string? CellId { get; set; }
        public string? Site { get; set; }
        public string? Sector { get; set; }
        public bool DeleteEntireSite { get; set; }
        public bool OptimizedOnly { get; set; }
    }
}
