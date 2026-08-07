using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class JobCompletionService(TranscoderDbContext db, IOptions<WorkerTimingOptions> timingOptions, TranscodePlanService planner, SystemSettingsService settings)
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
                    // Replacement is intentionally decoupled from the worker completion callback.
                    // AutoReplaceBackgroundService will pick this staged item up separately so the
                    // worker is never blocked waiting for a slow NAS/SMB move/replace operation.
                    job.LastMessage = "Output staged; replacement queued for the background auto-replace service.";
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (job.JobType == JobType.Probe && job.MediaItemId is not null)
        {
            var requestedAfterPlan = ReadRequestedWorkIntent(job.PayloadJson, "queueAfterPlanJobType");
            if (requestedAfterPlan is JobType.Cleanup or JobType.Transcode)
            {
                var plan = await planner.BuildAndQueuePlanForRequestedWorkAsync(
                    job.MediaItemId.Value,
                    requestedAfterPlan.Value,
                    force: true,
                    cancellationToken: cancellationToken);

                if (plan is not null)
                {
                    // If PlanReview is required this call will be rejected for now, but the
                    // requested work intent was attached to the PlanReview job by the planner
                    // and will continue automatically after approval.
                    _ = requestedAfterPlan == JobType.Cleanup
                        ? await planner.QueueCleanupFromExistingPlanAsync(job.MediaItemId.Value, cancellationToken)
                        : await planner.QueueTranscodeFromExistingPlanAsync(job.MediaItemId.Value, cancellationToken);
                }
            }
            else
            {
                await planner.BuildAndQueuePlanAsync(job.MediaItemId.Value, force: true, cancellationToken);
            }
        }

        return true;
    }

    public async Task<bool> FailAsync(long jobId, JobFailRequest request, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.LeaseId != request.LeaseId)
            return false;

        // A worker may time out after CompleteAsync has already committed success. Never let
        // a late failure callback undo a completed/staged/replaced job.
        if (job.Status == JobStatus.Completed)
            return true;

        job.LastError = $"{request.ErrorCode}: {request.Message}";
        job.LastMessage = request.Details;
        job.LeaseLastSeenUtc = DateTime.UtcNow;
        job.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(timingOptions.Value.MaxLeaseSeconds);

        if (IsInvalidStreamMapFailure(job, request))
        {
            await HandleInvalidStreamMapFailureAsync(job, request, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

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

    private async Task HandleInvalidStreamMapFailureAsync(JobEntity job, JobFailRequest request, CancellationToken cancellationToken)
    {
        if (job.MediaItemId is null)
        {
            MarkJobFailed(job);
            return;
        }

        var mediaId = job.MediaItemId.Value;
        var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media is null)
        {
            MarkJobFailed(job);
            return;
        }

        var alreadyRecovered = await db.Jobs.AnyAsync(x =>
            x.Id != job.Id &&
            x.MediaItemId == mediaId &&
            (x.JobType == JobType.Cleanup || x.JobType == JobType.Transcode) &&
            (EF.Functions.Like(x.LastError ?? string.Empty, "%InvalidStreamMap%") ||
             EF.Functions.Like(x.LastMessage ?? string.Empty, "%Stream map%matches no streams%")),
            cancellationToken);

        if (alreadyRecovered)
        {
            MarkJobFailed(job);
            media.Status = MediaStatus.NeedsReview;
            media.UpdatedUtc = DateTime.UtcNow;

            db.ReviewItems.Add(new Transcoder.Server.Data.Entities.ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = ReviewType.PlanReviewFailed,
                Severity = ReviewSeverity.Blocking,
                Reason = "Cleanup/transcode plan still produced an invalid ffmpeg stream map after reprobe.",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    jobId = job.Id,
                    jobType = job.JobType.ToString(),
                    errorCode = request.ErrorCode,
                    request.Message,
                    request.Details
                })
            });

            return;
        }

        job.Status = JobStatus.Cancelled;
        job.CompletedUtc = DateTime.UtcNow;
        job.Progress = null;
        job.LastMessage = "Cancelled stale cleanup/transcode plan because ffmpeg reported an invalid stream map. Fresh probe queued.";
        ClearLease(job);

        var staleJobs = await db.Jobs
            .Where(x =>
                x.Id != job.Id &&
                x.MediaItemId == mediaId &&
                x.Status != JobStatus.Completed &&
                x.Status != JobStatus.Cancelled &&
                x.Status != JobStatus.Failed &&
                (x.JobType == JobType.Cleanup ||
                 x.JobType == JobType.Transcode ||
                 x.JobType == JobType.PlanReview))
            .ToListAsync(cancellationToken);

        foreach (var staleJob in staleJobs)
        {
            staleJob.Status = JobStatus.Cancelled;
            staleJob.CompletedUtc = DateTime.UtcNow;
            staleJob.Progress = null;
            staleJob.LastMessage = "Cancelled because a fresh probe/replan was queued after an invalid ffmpeg stream map.";
            ClearLease(staleJob);
        }

        media.ProbeJson = null;
        media.PlanJson = null;
        media.PlanHash = null;
        media.PlanReviewJson = null;
        media.PlanCreatedUtc = null;
        media.PlanReviewedUtc = null;
        media.StagingPath = null;
        media.Status = MediaStatus.ProbeQueued;
        media.UpdatedUtc = DateTime.UtcNow;

        var probeAlreadyQueued = await db.Jobs.AnyAsync(x =>
            x.MediaItemId == media.Id &&
            x.JobType == JobType.Probe &&
            (x.Status == JobStatus.Queued ||
             x.Status == JobStatus.Leased ||
             x.Status == JobStatus.Running),
            cancellationToken);

        if (!probeAlreadyQueued)
        {
            db.Jobs.Add(new Transcoder.Server.Data.Entities.JobEntity
            {
                JobType = JobType.Probe,
                Status = JobStatus.Queued,
                LibraryId = media.LibraryId,
                MediaItemId = media.Id,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    libraryId = media.LibraryId,
                    mediaId = media.Id,
                    inputPath = media.FullPath,
                    fileSizeBytes = media.FileSizeBytes,
                    lastModifiedUtc = media.LastModifiedUtc
                }),
                CreatedUtc = DateTime.UtcNow,
                QueuedUtc = DateTime.UtcNow,
                LastMessage = "Queued fresh probe after invalid ffmpeg stream map."
            });
        }
    }

    private static bool IsInvalidStreamMapFailure(JobEntity job, JobFailRequest request)
    {
        if (job.JobType is not (JobType.Cleanup or JobType.Transcode))
            return false;

        if (job.MediaItemId is null)
            return false;

        if (string.Equals(request.ErrorCode, "InvalidStreamMap", StringComparison.OrdinalIgnoreCase))
            return true;

        var combined = $"{request.ErrorCode}\n{request.Message}\n{request.Details}";
        return combined.Contains("Stream map", StringComparison.OrdinalIgnoreCase) &&
               combined.Contains("matches no streams", StringComparison.OrdinalIgnoreCase);
    }

    private static void MarkJobFailed(JobEntity job)
    {
        job.Status = JobStatus.Failed;
        job.CompletedUtc = DateTime.UtcNow;
        job.Progress = null;
        ClearLease(job);
    }

    private static void ClearLease(JobEntity job)
    {
        job.LeaseId = null;
        job.LeasedByWorkerId = null;
        job.LeasedByWorkerInstanceId = null;
        job.LeaseStartedUtc = null;
        job.LeaseLastSeenUtc = null;
        job.LeaseExpiresUtc = null;
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

    private static JobType? ReadRequestedWorkIntent(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var value))
                return null;

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return Enum.TryParse<JobType>(text, ignoreCase: true, out var parsed)
                && parsed is JobType.Cleanup or JobType.Transcode
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
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
