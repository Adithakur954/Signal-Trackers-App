namespace SignalTracker.DTO.SitePrediction
{
    public class SitePredictionScenarioDto
    {
        public int scenario_id { get; set; }
        public string scenario_name { get; set; } = "";
        public string status { get; set; } = "updated";
        public int row_count { get; set; }
        public string? updated_at { get; set; }
    }
}
