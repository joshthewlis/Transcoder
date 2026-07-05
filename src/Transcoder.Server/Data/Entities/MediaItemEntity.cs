using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class MediaItemEntity
{
    public long Id { get; set; }
    public int LibraryId { get; set; }
    public LibraryEntity? Library { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public MediaStatus Status { get; set; } = MediaStatus.New;
    public long FileSizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public string? ProbeJson { get; set; }
    public string? PlanJson { get; set; }
    public string? PlanHash { get; set; }
    public string? PlanReviewJson { get; set; }
    public DateTime? PlanCreatedUtc { get; set; }
    public DateTime? PlanReviewedUtc { get; set; }
    public string? StagingPath { get; set; }
    public string? MetadataJson { get; set; }
    public string? OriginalLanguage { get; set; }
    public MetadataSource OriginalLanguageSource { get; set; } = MetadataSource.None;
    public string? MetadataMatchJson { get; set; }
    public DateTime? MetadataRefreshedUtc { get; set; }
    public string? MetadataError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
