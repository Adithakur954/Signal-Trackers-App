namespace SignalTracker.DTO.PythonBridge
{
    public class LteSitePredictionRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public string? Operator { get; set; }
        public string? PolygonIds { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }

    public class LteBuildingRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }

    public class LteBaselineRowsRequest
    {
        public long ProjectId { get; set; }
        public string? Region { get; set; }
        public string? JobId { get; set; }
        public string? Operator { get; set; }
        public long? LastId { get; set; }
        public int Limit { get; set; } = 50000;
        public int Offset { get; set; } = 0;
    }

    public class DictionaryRowsBulkRequest
    {
        public long ProjectId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string? Region { get; set; }
        public bool ReplaceExisting { get; set; }
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
    }

    public class LteOptimizationScenarioCreateRequest
    {
        public long ProjectId { get; set; }
        public int? ScenarioId { get; set; }
        public string? BaselineJobId { get; set; }
        public string? ScenarioName { get; set; }
        public string? ScenarioDescription { get; set; }
        public string? Region { get; set; }
        public string? Operator { get; set; }
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public double? ImpactRadiusM { get; set; }
        public int? NeighborSiteCount { get; set; }
        public int? MaxInterferenceSites { get; set; }
        public double? DeltaLat { get; set; }
        public double? DeltaLon { get; set; }
        public double? DeltaAzimuth { get; set; }
        public double? DeltaElectricalTilt { get; set; }
        public double? DeltaMechanicalTilt { get; set; }
        public double? DeltaTxPower { get; set; }
        public double? DeltaAntennaHeight { get; set; }
        public string? Status { get; set; }
        public string? CreatedBy { get; set; }
    }

    public class LteOptimizationScenarioStatusRequest
    {
        public long ScenarioRowId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? BaselineJobId { get; set; }
    }
}


