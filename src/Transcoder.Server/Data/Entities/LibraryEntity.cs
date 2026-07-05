using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class LibraryEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string PolicyJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public bool IsScanning { get; set; }
    public long FoldersScanned { get; set; }
    public long FilesDiscovered { get; set; }
    public long MediaItemsCreated { get; set; }
    public long MediaItemsUpdated { get; set; }
    public long ProbeJobsCreated { get; set; }
    public DateTime? ScanStartedUtc { get; set; }
    public DateTime? ScanCompletedUtc { get; set; }
    public string? ScanLastError { get; set; }
}
