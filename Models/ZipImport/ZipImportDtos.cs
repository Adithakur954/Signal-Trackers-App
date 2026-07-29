namespace SignalTracker.Models.ZipImport
{
    public sealed class ZipImportRequest
    {
        public IFormFile ZipFile { get; set; } = null!;
        public int? SessionId { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ZipImportSummary
    {
        public bool Success { get; set; }
        public int SessionId { get; set; }
        public long NetworkLogInserted { get; set; }
        public long NetworkNeighbourInserted { get; set; }
        public long SubSessionInserted { get; set; }
        public long CsPayloadRows { get; set; }
        public long PsPayloadRows { get; set; }
        public long EmptyPsPayloadRows { get; set; }
        public long DuplicatesSkipped { get; set; }
        public long InvalidRows { get; set; }
        public long FilesProcessed { get; set; }
        public long FilesSkipped { get; set; }
        public string ProcessingTime { get; set; } = "00:00:00";
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }
}
