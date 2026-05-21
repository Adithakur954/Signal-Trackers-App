namespace SignalTracker.Helper
{
    public static class FileValidator
    {
        private static readonly HashSet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Excel
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // .xlsx
        "application/vnd.ms-excel", // .xls

        // Word
        "application/msword", // .doc
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx

        // PDF
        "application/pdf", // .pdf

        // CSV
        "text/csv", // .csv

        // PowerPoint
        "application/vnd.ms-powerpoint", // .ppt
        "application/vnd.openxmlformats-officedocument.presentationml.presentation", // .pptx

        // Images
        "image/jpeg", // .jpg, .jpeg
        "image/png",  // .png
        "image/gif",  // .gif
        "image/bmp",  // .bmp

        // Zip
        //"application/zip" // .zip
    };

        public static bool IsValidContentType(string contentType)
        {
            return AllowedContentTypes.Contains(contentType);
        }
    }

}
