using System.Text.Json;
using System.Text.Json.Serialization;
using Transcoder.Contracts;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public sealed class MediaProcessingStats
{
    public long? OriginalSizeBytes { get; set; }
    public long? OutputSizeBytes { get; set; }
    public long? SavedBytes { get; set; }
    public long? CleanupSavedBytes { get; set; }
    public long? TranscodeSavedBytes { get; set; }
    public long? TotalSavedBytes { get; set; }
    public JobType? LastCompletedWorkType { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool StagingTransferComplete { get; set; }
    public string? StagingOutputPath { get; set; }
    public string? StagingCompleteMarkerPath { get; set; }
    public double? ElapsedSeconds { get; set; }
    public bool ReplacedOriginal { get; set; }
    public string? ReplacementBackupPath { get; set; }
    public DateTime? ReplacedUtc { get; set; }
    public List<ProcessingHistoryEntry> History { get; set; } = [];
}

public sealed class ProcessingHistoryEntry
{
    public string Stage { get; set; } = string.Empty;
    public JobType? JobType { get; set; }
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
    public long? BeforeSizeBytes { get; set; }
    public long? AfterSizeBytes { get; set; }
    public long? SavedBytes { get; set; }
    public long? TotalSavedBytes { get; set; }
    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public string? BackupPath { get; set; }
    public string? Message { get; set; }
}

public static class MediaProcessingStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static MediaProcessingStats Read(MediaItemEntity item)
    {
        if (string.IsNullOrWhiteSpace(item.MetadataJson))
            return new MediaProcessingStats();

        try
        {
            using var document = JsonDocument.Parse(item.MetadataJson);
            var root = document.RootElement;
            if (root.TryGetProperty("processingStats", out var nested))
                return JsonSerializer.Deserialize<MediaProcessingStats>(nested.GetRawText(), JsonOptions) ?? new MediaProcessingStats();

            return JsonSerializer.Deserialize<MediaProcessingStats>(root.GetRawText(), JsonOptions) ?? new MediaProcessingStats();
        }
        catch
        {
            return new MediaProcessingStats();
        }
    }

    public static void Write(MediaItemEntity item, MediaProcessingStats stats)
    {
        stats.History ??= [];
        item.MetadataJson = JsonSerializer.Serialize(new { processingStats = stats }, JsonOptions);
    }

    public static void AddHistory(MediaProcessingStats stats, ProcessingHistoryEntry entry)
    {
        stats.History ??= [];
        stats.History.Add(entry);
    }

    public static MediaBrowserTotalsDto BuildTotals(IEnumerable<MediaItemEntity> items)
    {
        var totals = new MediaBrowserTotalsDto();
        var outputKnown = true;
        foreach (var item in items)
        {
            totals.ItemCount++;
            totals.OriginalSizeBytes += item.FileSizeBytes;

            var stats = Read(item);
            var totalSaved = stats.ReplacedOriginal ? stats.TotalSavedBytes ?? stats.SavedBytes : null;
            if (totalSaved is not null)
            {
                totals.CompletedCount++;
                totals.ActualSavedBytes += totalSaved.Value;
            }
            if (stats.ReplacedOriginal && stats.CleanupSavedBytes is not null)
                totals.CleanupSavedBytes += stats.CleanupSavedBytes.Value;
            if (stats.ReplacedOriginal && stats.TranscodeSavedBytes is not null)
                totals.TranscodeSavedBytes += stats.TranscodeSavedBytes.Value;

            if (stats.ReplacedOriginal && stats.OutputSizeBytes is not null)
            {
                totals.ActualOutputSizeBytes = (totals.ActualOutputSizeBytes ?? 0) + stats.OutputSizeBytes.Value;
            }
            else
            {
                outputKnown = false;
            }

            var planSummary = ReadPlanSummary(item.PlanJson);
            if (planSummary.EstimatedRemovedBytes is not null)
            {
                totals.EstimatedCleanupSavingsBytes += planSummary.EstimatedRemovedBytes.Value;
                if (!planSummary.EstimatedSavingsComplete)
                    totals.EstimatedCleanupSavingsComplete = false;
            }
        }

        if (!outputKnown && totals.ActualOutputSizeBytes == 0)
            totals.ActualOutputSizeBytes = null;

        return totals;
    }

    public static PlanSummary ReadPlanSummary(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
            return new PlanSummary();

        try
        {
            var plan = JsonSerializer.Deserialize<TranscodePlanDto>(planJson, JsonOptions);
            return new PlanSummary
            {
                PlanKind = plan?.PlanKind,
                EstimatedRemovedBytes = plan?.EstimatedRemovedBytes,
                EstimatedOutputSizeBytes = plan?.EstimatedOutputSizeBytes,
                EstimatedSavingsComplete = plan?.EstimatedSavingsComplete ?? false
            };
        }
        catch
        {
            return new PlanSummary();
        }
    }

    public sealed class PlanSummary
    {
        public string? PlanKind { get; init; }
        public long? EstimatedRemovedBytes { get; init; }
        public long? EstimatedOutputSizeBytes { get; init; }
        public bool EstimatedSavingsComplete { get; init; }
    }
}
