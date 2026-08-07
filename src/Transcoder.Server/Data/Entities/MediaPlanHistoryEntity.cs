namespace Transcoder.Server.Data.Entities;

public sealed class MediaPlanHistoryEntity
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public MediaItemEntity? MediaItem { get; set; }

    public int Revision { get; set; }
    public bool IsCurrent { get; set; }

    public string PlanJson { get; set; } = string.Empty;
    public string? PlanHash { get; set; }
    public string? PlanKind { get; set; }
    public string? ProcessingStrategy { get; set; }

    public long InputFileSizeBytes { get; set; }
    public long? EstimatedRemovedBytes { get; set; }
    public long? EstimatedOutputSizeBytes { get; set; }
    public bool EstimatedSavingsComplete { get; set; }
    public bool CleanupRequired { get; set; }

    public string? PlanReviewJson { get; set; }
    public DateTime PlanCreatedUtc { get; set; }
    public DateTime? PlanReviewedUtc { get; set; }
    public DateTime? SupersededUtc { get; set; }
}
