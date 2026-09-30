using System;
using System.Collections.Concurrent;
using System.IO;

namespace SignalTracker.Services
{
    public sealed class ReportProgressItem
    {
        public string JobId { get; set; } = string.Empty;
        public int Progress { get; set; } // 0 - 100
        public string Stage { get; set; } = "Initializing";
        public int Status { get; set; } = 2; // 2 = Processing, 1 = Completed, 0 = Failed
        public string? Error { get; set; }
        public string? FileName { get; set; }
        public string? DownloadPath { get; set; }
        public byte[]? FileBytes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        public bool IsCompleted => Status == 1;
        public bool IsFailed => Status == 0;
        public bool IsProcessing => Status == 2;
    }

    public static class ReportProgressTracker
    {
        private static readonly ConcurrentDictionary<string, ReportProgressItem> _jobs = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastCleanup = DateTime.UtcNow;

        public static ReportProgressItem GetOrCreate(string jobId)
        {
            CleanupOldJobs();
            return _jobs.GetOrAdd(jobId, id => new ReportProgressItem
            {
                JobId = id,
                Progress = 0,
                Stage = "Starting...",
                Status = 2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public static void Update(string jobId, int progress, string stage, int status = 2, string? fileName = null, string? downloadPath = null, string? error = null)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;

            CleanupOldJobs();

            _jobs.AddOrUpdate(jobId,
                id => new ReportProgressItem
                {
                    JobId = id,
                    Progress = Math.Clamp(progress, 0, 100),
                    Stage = stage,
                    Status = status,
                    FileName = fileName,
                    DownloadPath = downloadPath,
                    Error = error,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CompletedAt = (status == 1 || status == 0) ? DateTime.UtcNow : null
                },
                (id, existing) =>
                {
                    existing.Progress = Math.Clamp(progress, 0, 100);
                    existing.Stage = stage;
                    existing.Status = status;
                    if (!string.IsNullOrWhiteSpace(fileName)) existing.FileName = fileName;
                    if (!string.IsNullOrWhiteSpace(downloadPath)) existing.DownloadPath = downloadPath;
                    if (!string.IsNullOrWhiteSpace(error)) existing.Error = error;
                    existing.UpdatedAt = DateTime.UtcNow;
                    if (status == 1 || status == 0) existing.CompletedAt = DateTime.UtcNow;
                    return existing;
                });
        }

        public static void Complete(string jobId, string fileName, string? downloadPath = null, byte[]? bytes = null)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;

            _jobs.AddOrUpdate(jobId,
                id => new ReportProgressItem
                {
                    JobId = id,
                    Progress = 100,
                    Stage = "Completed",
                    Status = 1,
                    FileName = fileName,
                    DownloadPath = downloadPath,
                    FileBytes = bytes,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                },
                (id, existing) =>
                {
                    existing.Progress = 100;
                    existing.Stage = "Completed";
                    existing.Status = 1;
                    existing.FileName = fileName;
                    if (!string.IsNullOrWhiteSpace(downloadPath)) existing.DownloadPath = downloadPath;
                    if (bytes != null) existing.FileBytes = bytes;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.CompletedAt = DateTime.UtcNow;
                    return existing;
                });
        }

        public static void Fail(string jobId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;

            _jobs.AddOrUpdate(jobId,
                id => new ReportProgressItem
                {
                    JobId = id,
                    Progress = 100,
                    Stage = "Failed",
                    Status = 0,
                    Error = errorMessage,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                },
                (id, existing) =>
                {
                    existing.Status = 0;
                    existing.Stage = "Failed";
                    existing.Error = errorMessage;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.CompletedAt = DateTime.UtcNow;
                    return existing;
                });
        }

        public static ReportProgressItem? Get(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return null;
            _jobs.TryGetValue(jobId, out var item);
            return item;
        }

        public static bool Remove(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return false;
            if (_jobs.TryRemove(jobId, out var item))
            {
                if (!string.IsNullOrWhiteSpace(item.DownloadPath) && File.Exists(item.DownloadPath))
                {
                    try { File.Delete(item.DownloadPath); } catch { }
                }
                return true;
            }
            return false;
        }

        private static void CleanupOldJobs()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCleanup).TotalMinutes < 15) return;
            _lastCleanup = now;

            var cutoff = now.AddHours(-2);
            foreach (var kvp in _jobs)
            {
                if (kvp.Value.UpdatedAt < cutoff)
                {
                    if (_jobs.TryRemove(kvp.Key, out var item))
                    {
                        if (!string.IsNullOrWhiteSpace(item.DownloadPath) && File.Exists(item.DownloadPath))
                        {
                            try { File.Delete(item.DownloadPath); } catch { }
                        }
                    }
                }
            }
        }
    }
}
