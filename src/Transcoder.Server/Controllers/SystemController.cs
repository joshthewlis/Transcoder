using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(TranscoderDbContext db, SystemSettingsService settings, TranscodePlanService planner) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<SystemStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
        var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);
        var mediaItemsForStats = await db.MediaItems.AsNoTracking().Where(x => x.MetadataJson != null && x.MetadataJson != "").ToListAsync(cancellationToken);
        var stats = mediaItemsForStats.Select(MediaProcessingStatsStore.Read).ToList();
        var dto = new SystemStatusDto(
            ServerVersion: typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ApiVersion: "1",
            ProcessingMode: mode,
            DatabaseProvider: db.Database.ProviderName ?? "Unknown",
            WorkersOnline: await db.Workers.CountAsync(x => x.State == WorkerState.Online, cancellationToken),
            WorkersUnresponsive: await db.Workers.CountAsync(x => x.State == WorkerState.Unresponsive, cancellationToken),
            WorkersLost: await db.Workers.CountAsync(x => x.State == WorkerState.Lost, cancellationToken),
            QueuedJobs: await db.Jobs.CountAsync(x => x.Status == JobStatus.Queued, cancellationToken),
            RunningJobs: await db.Jobs.CountAsync(x => x.Status == JobStatus.Running || x.Status == JobStatus.Leased, cancellationToken),
            NeedsReview: await db.ReviewItems.CountAsync(x => !x.Resolved, cancellationToken),
            AutoQueueCleanupJobs: execution.AutoQueueCleanupJobs,
            AutoQueueTranscodeJobs: execution.AutoQueueTranscodeJobs,
            RequirePlanReviewBeforeAutoQueue: execution.RequirePlanReviewBeforeAutoQueue,
            ActiveHours: activeHours,
            TotalActualSavedBytes: stats.Where(x => x.ReplacedOriginal && (x.TotalSavedBytes ?? x.SavedBytes) is not null).Sum(x => (x.TotalSavedBytes ?? x.SavedBytes)!.Value),
            CompletedCleanupCount: stats.Count(x => x.ReplacedOriginal && x.LastCompletedWorkType == JobType.Cleanup),
            CompletedTranscodeCount: stats.Count(x => x.ReplacedOriginal && x.LastCompletedWorkType == JobType.Transcode));

        return dto;
    }

    [HttpPost("stats/recalculate-savings")]
    public async Task<ActionResult<object>> RecalculateSavings(CancellationToken cancellationToken)
    {
        var items = await db.MediaItems
            .Where(x => x.MetadataJson != null && x.MetadataJson != "")
            .ToListAsync(cancellationToken);

        long beforeSavedBytes = 0;
        long afterSavedBytes = 0;
        var recalculated = 0;
        var clearedUnreplaced = 0;

        foreach (var item in items)
        {
            var stats = MediaProcessingStatsStore.Read(item);
            var previousSaved = stats.TotalSavedBytes ?? stats.SavedBytes;
            if (previousSaved is > 0)
                beforeSavedBytes += previousSaved.Value;

            if (!stats.ReplacedOriginal)
            {
                if (stats.TotalSavedBytes is not null || stats.SavedBytes is not null || stats.CleanupSavedBytes is not null || stats.TranscodeSavedBytes is not null)
                    clearedUnreplaced++;

                stats.TotalSavedBytes = null;
                stats.SavedBytes = null;
                stats.CleanupSavedBytes = null;
                stats.TranscodeSavedBytes = null;
                MediaProcessingStatsStore.Write(item, stats);
                continue;
            }

            var originalSize = stats.OriginalSizeBytes;
            if (originalSize is null)
            {
                originalSize = stats.History
                    .Where(x => x.BeforeSizeBytes is not null)
                    .OrderBy(x => x.CompletedUtc)
                    .Select(x => x.BeforeSizeBytes)
                    .FirstOrDefault();
            }

            originalSize ??= item.FileSizeBytes;
            var outputSize = item.FileSizeBytes;
            var savedBytes = Math.Max(0, originalSize.Value - outputSize);

            stats.OriginalSizeBytes = originalSize;
            stats.OutputSizeBytes = outputSize;
            stats.SavedBytes = savedBytes;
            stats.TotalSavedBytes = savedBytes;

            if (stats.LastCompletedWorkType == JobType.Transcode)
            {
                stats.TranscodeSavedBytes = savedBytes;
                stats.CleanupSavedBytes = null;
            }
            else
            {
                stats.CleanupSavedBytes = savedBytes;
                stats.TranscodeSavedBytes = null;
            }

            MediaProcessingStatsStore.Write(item, stats);
            afterSavedBytes += savedBytes;
            recalculated++;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            considered = items.Count,
            recalculated,
            clearedUnreplaced,
            beforeSavedBytes,
            afterSavedBytes,
            deltaBytes = afterSavedBytes - beforeSavedBytes,
            message = "Savings totals recalculated from replaced media only. Staged-but-not-replaced outputs are no longer counted as actual saved space."
        });
    }

    [HttpGet("mode")]
    public async Task<ActionResult<SetProcessingModeRequest>> GetMode(CancellationToken cancellationToken) =>
        new SetProcessingModeRequest(await settings.GetProcessingModeAsync(cancellationToken));

    [HttpPut("mode")]
    public async Task<ActionResult<AutomationReconciliationResultDto>> SetMode(SetProcessingModeRequest request, CancellationToken cancellationToken)
    {
        await settings.SetProcessingModeAsync(request.ProcessingMode, cancellationToken);
        return await planner.ReconcileAutomaticQueuesAsync(cancellationToken);
    }

    [HttpGet("execution")]
    public async Task<ActionResult<ExecutionSettingsDto>> GetExecution(CancellationToken cancellationToken) =>
        await settings.GetExecutionSettingsAsync(cancellationToken);

    [HttpPut("execution")]
    public async Task<ActionResult<AutomationReconciliationResultDto>> SetExecution(ExecutionSettingsDto request, CancellationToken cancellationToken)
    {
        await settings.SetExecutionSettingsAsync(request, cancellationToken);
        return await planner.ReconcileAutomaticQueuesAsync(cancellationToken);
    }

    [HttpPost("automation/reconcile")]
    public async Task<ActionResult<AutomationReconciliationResultDto>> ReconcileAutomation(CancellationToken cancellationToken) =>
        await planner.ReconcileAutomaticQueuesAsync(cancellationToken);
}
