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
            TotalActualSavedBytes: stats.Where(x => (x.TotalSavedBytes ?? x.SavedBytes) is not null).Sum(x => (x.TotalSavedBytes ?? x.SavedBytes)!.Value),
            CompletedCleanupCount: stats.Count(x => x.LastCompletedWorkType == JobType.Cleanup),
            CompletedTranscodeCount: stats.Count(x => x.LastCompletedWorkType == JobType.Transcode));

        return dto;
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
