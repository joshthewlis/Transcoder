namespace Transcoder.Contracts;

public sealed class MediaItemDto
{
    public long Id { get; set; }
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public MediaStatus Status { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public bool HasProbe { get; set; }
    public bool HasPlan { get; set; }
    public string? PlanHash { get; set; }
    public string? StagingPath { get; set; }
    public long? ActualOutputSizeBytes { get; set; }
    public long? ActualSavedBytes { get; set; }
    public long? ActualCleanupSavedBytes { get; set; }
    public long? ActualTranscodeSavedBytes { get; set; }
    public long? ActualTotalSavedBytes { get; set; }
    public JobType? LastCompletedWorkType { get; set; }
    public DateTime? LastWorkCompletedUtc { get; set; }
    public bool StagingTransferComplete { get; set; }
    public string? StagingCompleteMarkerPath { get; set; }
    public bool ReplacedOriginal { get; set; }
    public string? ReplacementBackupPath { get; set; }
    public DateTime? ReplacedUtc { get; set; }
    public string? PlanKind { get; set; }
    public long? EstimatedCleanupSavingsBytes { get; set; }
    public long? EstimatedCleanupOutputBytes { get; set; }
    public bool EstimatedCleanupSavingsComplete { get; set; }
    public string? OriginalLanguage { get; set; }
    public MetadataSource OriginalLanguageSource { get; set; }
    public DateTime? MetadataRefreshedUtc { get; set; }
    public string? MetadataError { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ReviewItemDto
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string? MediaName { get; set; }
    public string? MediaRelativePath { get; set; }
    public string? MediaFullPath { get; set; }
    public ReviewType ReviewType { get; set; }
    public ReviewSeverity Severity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public bool Resolved { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class PagedResultDto<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

public sealed class FileSystemEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
}



public sealed class QueueMediaWorkResultDto
{
    public long MediaId { get; set; }
    public bool Accepted { get; set; }
    public bool Queued { get; set; }
    public bool AlreadyQueued { get; set; }
    public JobType? JobType { get; set; }
    public MediaStatus? MediaStatus { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class QueueLibraryWorkResultDto
{
    public int LibraryId { get; set; }
    public JobType JobType { get; set; }
    public int Considered { get; set; }
    public int Queued { get; set; }
    public int AlreadyQueued { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; set; } = [];
}


public sealed class ReplaceMediaResultDto
{
    public long MediaId { get; set; }
    public bool Accepted { get; set; }
    public bool Replaced { get; set; }
    public string Message { get; set; } = string.Empty;
    public MediaStatus? MediaStatus { get; set; }
    public string? OriginalPath { get; set; }
    public string? StagingPath { get; set; }
    public string? BackupPath { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public long? ReplacementSizeBytes { get; set; }
    public long? TotalSavedBytes { get; set; }
}

public sealed class ReplaceLibraryResultDto
{
    public int LibraryId { get; set; }
    public int Considered { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; set; } = [];
}

public sealed class SetMediaMetadataRequest
{
    public string? OriginalLanguage { get; set; }
    public MetadataSource OriginalLanguageSource { get; set; } = MetadataSource.Manual;
}


public sealed class MediaBrowserDto
{
    public int LibraryId { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<MediaBrowserCrumbDto> Breadcrumbs { get; set; } = [];
    public MediaBrowserTotalsDto Totals { get; set; } = new();
    public List<MediaBrowserDirectoryDto> Directories { get; set; } = [];
    public List<MediaItemDto> Items { get; set; } = [];
}

public sealed class MediaBrowserCrumbDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class MediaBrowserDirectoryDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public MediaBrowserTotalsDto Totals { get; set; } = new();
}

public sealed class MediaBrowserTotalsDto
{
    public int ItemCount { get; set; }
    public int CompletedCount { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long? ActualOutputSizeBytes { get; set; }
    public long ActualSavedBytes { get; set; }
    public long CleanupSavedBytes { get; set; }
    public long TranscodeSavedBytes { get; set; }
    public long EstimatedCleanupSavingsBytes { get; set; }
    public bool EstimatedCleanupSavingsComplete { get; set; } = true;
}
