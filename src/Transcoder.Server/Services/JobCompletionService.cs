using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class JobCompletionService(TranscoderDbContext db, IOptions<WorkerTimingOptions> timingOptions, TranscodePlanService planner, SystemSettingsService settings, ReplacementService replacement)
{
    public async Task<bool> ProgressAsync(long jobId, JobProgressRequest request, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.LeaseId != request.LeaseId)
            return false;

        job.Status = JobStatus.Running;
        job.Progress = request.Progress;
        job.LastMessage = request.Message;
        job.LeaseLastSeenUtc = DateTime.UtcNow;
        job.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(timingOptions.Value.MaxLeaseSeconds);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(long jobId, JobCompleteRequest request, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.LeaseId != request.LeaseId)
            return false;

        job.Status = JobStatus.Completed;
        job.CompletedUtc = DateTime.UtcNow;
        job.Progress = 100;
        job.ResultJson = request.Result.GetRawText();
        job.LeaseLastSeenUtc = DateTime.UtcNow;
        job.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(timingOptions.Value.MaxLeaseSeconds);

        if (job.JobType == JobType.Probe && job.MediaItemId is not null)
        {
            var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == job.MediaItemId.Value, cancellationToken);
            if (media is not null)
            {
                media.ProbeJson = request.Result.GetRawText();
                media.Status = MediaStatus.Probed;
                media.UpdatedUtc = DateTime.UtcNow;
            }
        }

        if (job.JobType == JobType.PlanReview)
        {
            await planner.HandlePlanReviewCompleteAsync(job, request.Result, cancellationToken);
        }

        if ((job.JobType == JobType.Cleanup || job.JobType == JobType.Transcode) && job.MediaItemId is not null)
        {
            var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == job.MediaItemId.Value, cancellationToken);
            if (media is not null)
            {
                var transferComplete = TryGetBool(request.Result, "stagingTransferComplete") ?? false;
                var stagingPath = TryGetString(request.Result, "stagingOutputPath");
                var outputSize = TryGetLong(request.Result, "outputFileSizeBytes");
                var inputSize = TryGetLong(request.Result, "inputFileSizeBytes") ?? media.FileSizeBytes;
                long? savedBytes = outputSize.HasValue
                    ? (long?)Math.Max(0, inputSize - outputSize.Value)
                    : null;

                var existingStats = MediaProcessingStatsStore.Read(media);
                var originalSize = existingStats.OriginalSizeBytes ?? media.FileSizeBytes;
                var cleanupSavedBytes = existingStats.CleanupSavedBytes;
                var transcodeSavedBytes = existingStats.TranscodeSavedBytes;

                if (job.JobType == JobType.Cleanup)
                    cleanupSavedBytes = savedBytes;
                if (job.JobType == JobType.Transcode)
                    transcodeSavedBytes = savedBytes;

                long? totalSavedBytes = outputSize.HasValue
                    ? (long?)Math.Max(0, originalSize - outputSize.Value)
                    : existingStats.TotalSavedBytes ?? existingStats.SavedBytes;

                existingStats.History ??= [];
                var processingStats = new MediaProcessingStats
                {
                    OriginalSizeBytes = originalSize,
                    OutputSizeBytes = outputSize,
                    SavedBytes = savedBytes,
                    CleanupSavedBytes = cleanupSavedBytes,
                    TranscodeSavedBytes = transcodeSavedBytes,
                    TotalSavedBytes = totalSavedBytes,
                    LastCompletedWorkType = job.JobType,
                    CompletedUtc = DateTime.UtcNow,
                    StagingTransferComplete = transferComplete,
                    StagingOutputPath = stagingPath,
                    StagingCompleteMarkerPath = TryGetString(request.Result, "stagingCompleteMarkerPath"),
                    ElapsedSeconds = TryGetDouble(request.Result, "elapsedSeconds"),
                    ReplacedOriginal = existingStats.ReplacedOriginal,
                    ReplacementBackupPath = existingStats.ReplacementBackupPath,
                    ReplacedUtc = existingStats.ReplacedUtc,
                    History = existingStats.History
                };
                MediaProcessingStatsStore.AddHistory(processingStats, new ProcessingHistoryEntry
                {
                    Stage = job.JobType == JobType.Cleanup ? "CleanupToStaging" : "TranscodeToStaging",
                    JobType = job.JobType,
                    CompletedUtc = DateTime.UtcNow,
                    BeforeSizeBytes = inputSize,
                    AfterSizeBytes = outputSize,
                    SavedBytes = savedBytes,
                    TotalSavedBytes = totalSavedBytes,
                    InputPath = TryGetString(request.Result, "inputPath"),
                    OutputPath = stagingPath,
                    Message = transferComplete ? "Output validated and staged." : "Output completed without a confirmed staging transfer marker."
                });
                MediaProcessingStatsStore.Write(media, processingStats);

                media.Status = transferComplete
                    ? job.JobType == JobType.Cleanup ? MediaStatus.StagedCleaned : MediaStatus.Staged
                    : MediaStatus.NeedsReview;
                media.UpdatedUtc = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(stagingPath))
                    media.StagingPath = stagingPath;

                if (!transferComplete)
                {
                    db.ReviewItems.Add(new Transcoder.Server.Data.Entities.ReviewItemEntity
                    {
                        MediaItemId = media.Id,
                        ReviewType = ReviewType.OutputValidationWarning,
                        Severity = ReviewSeverity.Blocking,
                        Reason = "Worker completed work without a confirmed staging transfer marker.",
                        DetailsJson = request.Result.GetRawText()
                    });
                }
                else if (await settings.GetProcessingModeAsync(cancellationToken) == ProcessingMode.ReplaceApproved)
                {
                    var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
                    var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);
                    if (activeHours.AllowStagedWork)
                    {
                        await db.SaveChangesAsync(cancellationToken);
                        await replacement.ReplaceMediaAsync(media.Id, cancellationToken);
                        return true;
                    }

                    job.LastMessage = $"Replacement deferred by active hours. {activeHours.Message}";
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (job.JobType == JobType.Probe && job.MediaItemId is not null)
        {
            await planner.BuildAndQueuePlanAsync(job.MediaItemId.Value, force: true, cancellationToken);
        }

        return true;
    }

    public async Task<bool> FailAsync(long jobId, JobFailRequest request, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.LeaseId != request.LeaseId)
            return false;

        job.LastError = $"{request.ErrorCode}: {request.Message}";
        job.LastMessage = request.Details;
        job.LeaseLastSeenUtc = DateTime.UtcNow;
        job.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(timingOptions.Value.MaxLeaseSeconds);

        if (job.AttemptNumber < job.MaxAttempts)
        {
            job.Status = JobStatus.Queued;
            job.LeaseId = null;
            job.LeasedByWorkerId = null;
            job.LeasedByWorkerInstanceId = null;
            job.LeaseStartedUtc = null;
            job.LeaseLastSeenUtc = null;
            job.LeaseExpiresUtc = null;
            job.Progress = null;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.CompletedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? TryGetString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static long? TryGetLong(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static double? TryGetDouble(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == System.Text.Json.JsonValueKind.String && double.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static bool? TryGetBool(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        if (value.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }
}
