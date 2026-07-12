using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class TranscodePlanService(
    TranscoderDbContext db,
    SystemSettingsService settings,
    IOptions<StorageOptions> storageOptions,
    MetadataRefreshService metadataRefresh,
    ILogger<TranscodePlanService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public async Task<TranscodePlanDto?> BuildAndQueuePlanAsync(long mediaId, bool force = false, CancellationToken cancellationToken = default)
    {
        var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media?.Library is null)
            return null;

        if (string.IsNullOrWhiteSpace(media.ProbeJson))
        {
            await QueueProbeIfMissingAsync(media, cancellationToken);
            return null;
        }

        if (!force && !string.IsNullOrWhiteSpace(media.PlanJson))
            return JsonSerializer.Deserialize<TranscodePlanDto>(media.PlanJson, JsonOptions);

        var policy = DeserializePolicy(media.Library.PolicyJson);
        await metadataRefresh.TryRefreshTrackedMediaAsync(media, policy, force: false, cancellationToken);

        var probe = ParseProbe(media.ProbeJson);
        var needsVideoProfile = policy.ProcessingStrategy != ProcessingStrategy.CleanupOnly;
        var profileSelection = needsVideoProfile
            ? await SelectVideoProfileAsync(policy, cancellationToken)
            : ProfileSelection.Copy("Cleanup/remux plans copy video and do not require a CPU or GPU encoder.");

        var plan = BuildPlan(media, media.Library, policy, profileSelection, probe);
        plan.PlanHash = ComputePlanHash(plan);

        media.PlanJson = JsonSerializer.Serialize(plan, JsonOptions);
        media.PlanHash = plan.PlanHash;
        media.PlanCreatedUtc = DateTime.UtcNow;
        media.StagingPath = plan.StagingOutputPath;
        media.UpdatedUtc = DateTime.UtcNow;

        await ClearExistingPlanningReviewsAsync(media.Id, cancellationToken);

        if (plan.BlockingReasons.Count > 0)
        {
            media.Status = MediaStatus.NeedsReview;
            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = ReviewType.PolicyAmbiguous,
                Severity = ReviewSeverity.Blocking,
                Reason = string.Join("; ", plan.BlockingReasons.Take(3)),
                DetailsJson = JsonSerializer.Serialize(new { plan, reasons = plan.BlockingReasons }, JsonOptions)
            });
            await db.SaveChangesAsync(cancellationToken);
            return plan;
        }

        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        if (!IsNoActionPlan(plan) && policy.Review.PlanReviewRequired && (mode is ProcessingMode.PlanAndReview or ProcessingMode.TranscodeToStaging or ProcessingMode.ReplaceApproved))
        {
            media.Status = MediaStatus.Planning;
            QueuePlanReview(media, plan);
        }
        else
        {
            media.Status = IsNoActionPlan(plan) ? MediaStatus.Skipped : IsCleanupPlan(plan) ? MediaStatus.ReadyToCleanup : MediaStatus.ReadyToTranscode;
            if (!IsNoActionPlan(plan))
                await QueueWorkIfAutoAllowedAsync(media, plan, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return plan;
    }

    public async Task HandlePlanReviewCompleteAsync(JobEntity job, JsonElement result, CancellationToken cancellationToken = default)
    {
        if (job.MediaItemId is null)
            return;

        var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == job.MediaItemId.Value, cancellationToken);
        if (media is null)
            return;

        media.PlanReviewJson = result.GetRawText();
        media.PlanReviewedUtc = DateTime.UtcNow;
        media.UpdatedUtc = DateTime.UtcNow;

        var reviewStatus = GetString(result, "reviewStatus") ?? "Rejected";
        if (reviewStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            var plan = string.IsNullOrWhiteSpace(media.PlanJson)
                ? null
                : JsonSerializer.Deserialize<TranscodePlanDto>(media.PlanJson, JsonOptions);

            media.Status = plan is not null && IsNoActionPlan(plan)
                ? MediaStatus.Skipped
                : plan is not null && IsCleanupPlan(plan)
                    ? MediaStatus.ReadyToCleanup
                    : MediaStatus.ReadyToTranscode;

            await QueueWorkIfAutoAllowedAsync(media, plan, cancellationToken);
        }
        else
        {
            media.Status = MediaStatus.NeedsReview;
            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = ReviewType.PlanReviewFailed,
                Severity = ReviewSeverity.Blocking,
                Reason = "Plan review did not approve the generated plan.",
                DetailsJson = result.GetRawText()
            });
        }
    }

    public async Task<AutomationReconciliationResultDto> ReconcileAutomaticQueuesAsync(CancellationToken cancellationToken = default)
    {
        var result = new AutomationReconciliationResultDto();
        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);

        if (mode is not (ProcessingMode.TranscodeToStaging or ProcessingMode.ReplaceApproved))
        {
            result.Messages.Add("Automation reconciliation skipped because staged work is not enabled.");
            return result;
        }

        if (!execution.AutoQueueCleanupJobs && !execution.AutoQueueTranscodeJobs)
        {
            result.Messages.Add("Automation reconciliation skipped because auto cleanup/transcode queueing is disabled.");
            return result;
        }

        var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);
        result.ActiveHours = activeHours;
        if (!activeHours.AllowStagedWork)
        {
            result.ActiveHoursPaused = true;
            result.Messages.Add($"Automation reconciliation skipped because active hours are closed. {activeHours.Message}");
            return result;
        }

        var mediaIds = await db.MediaItems.AsNoTracking()
            .Where(x => x.PlanJson != null && x.PlanJson != "")
            .Where(x => x.Status == MediaStatus.ReadyToCleanup || x.Status == MediaStatus.ReadyToTranscode || x.Status == MediaStatus.Probed || x.Status == MediaStatus.Planning)
            .OrderBy(x => x.UpdatedUtc)
            .Select(x => x.Id)
            .Take(1000)
            .ToListAsync(cancellationToken);

        result.Considered = mediaIds.Count;

        foreach (var mediaId in mediaIds)
        {
            var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
            if (media?.Library is null || string.IsNullOrWhiteSpace(media.PlanJson))
            {
                result.Skipped++;
                continue;
            }

            TranscodePlanDto? plan;
            try
            {
                plan = JsonSerializer.Deserialize<TranscodePlanDto>(media.PlanJson, JsonOptions);
            }
            catch
            {
                result.Skipped++;
                continue;
            }

            if (plan is null || IsNoActionPlan(plan) || (plan.BlockingReasons.Count > 0 && !HasApprovedPlanReview(media)))
            {
                result.Skipped++;
                continue;
            }

            var policy = DeserializePolicy(media.Library.PolicyJson);
            policy.Execution ??= new ExecutionPolicyDto();

            if (execution.RequirePlanReviewBeforeAutoQueue && policy.Review.PlanReviewRequired && !HasApprovedPlanReview(media))
            {
                result.Skipped++;
                continue;
            }

            var wantedType = IsCleanupPlan(plan) ? JobType.Cleanup : JobType.Transcode;
            if (wantedType == JobType.Cleanup && (!execution.AutoQueueCleanupJobs || !policy.Execution.GenerateCleanupJobs))
            {
                result.Skipped++;
                continue;
            }

            if (wantedType == JobType.Transcode && (!execution.AutoQueueTranscodeJobs || !policy.Execution.GenerateTranscodeJobs))
            {
                result.Skipped++;
                continue;
            }

            var item = await QueueExistingPlanWorkAsync(media.Id, wantedType, cancellationToken);
            if (item.Queued)
            {
                if (wantedType == JobType.Cleanup) result.QueuedCleanup++;
                else result.QueuedTranscode++;
                logger.LogInformation("Auto-queued {JobType} for {Media} in library {Library}", wantedType, media.RelativePath, media.Library.Name);
            }
            else if (item.AlreadyQueued)
            {
                result.AlreadyQueued++;
            }
            else
            {
                result.Skipped++;
                if (result.Messages.Count < 10)
                    result.Messages.Add($"{media.RelativePath}: {item.Message}");
            }
        }

        if (result.Messages.Count == 0)
            result.Messages.Add($"Automation considered {result.Considered} item(s), queued {result.QueuedCleanup} cleanup and {result.QueuedTranscode} transcode job(s).");

        return result;
    }

    public Task<QueueMediaWorkResultDto> QueueCleanupFromExistingPlanAsync(long mediaId, CancellationToken cancellationToken = default) =>
        QueueExistingPlanWorkAsync(mediaId, JobType.Cleanup, cancellationToken);

    public Task<QueueMediaWorkResultDto> QueueTranscodeFromExistingPlanAsync(long mediaId, CancellationToken cancellationToken = default) =>
        QueueExistingPlanWorkAsync(mediaId, JobType.Transcode, cancellationToken);

    public async Task<QueueLibraryWorkResultDto> QueueLibraryCleanupFromExistingPlansAsync(int libraryId, CancellationToken cancellationToken = default) =>
        await QueueLibraryExistingPlanWorkAsync(libraryId, JobType.Cleanup, cancellationToken);

    public async Task<QueueLibraryWorkResultDto> QueueLibraryTranscodeFromExistingPlansAsync(int libraryId, CancellationToken cancellationToken = default) =>
        await QueueLibraryExistingPlanWorkAsync(libraryId, JobType.Transcode, cancellationToken);

    public async Task<PilotRunResultDto> QueueLibraryPilotRunAsync(int libraryId, PilotRunRequestDto? request, CancellationToken cancellationToken = default)
    {
        request ??= new PilotRunRequestDto();
        var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 5 : request.MaxFiles, 1, 25);
        var maxEstimatedStagingBytes = request.MaxEstimatedStagingBytes is > 0 ? request.MaxEstimatedStagingBytes : null;

        var minEstimatedSavingBytes = request.MinEstimatedSavingBytes is > 0 ? request.MinEstimatedSavingBytes.Value : 0;

        var activeMediaIds = await db.Jobs.AsNoTracking()
            .Where(x => x.LibraryId == libraryId
                && x.MediaItemId != null
                && (x.JobType == JobType.Cleanup || x.JobType == JobType.Transcode)
                && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .Select(x => x.MediaItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var activeMediaIdSet = activeMediaIds.ToHashSet();

        var exists = await db.Libraries.AnyAsync(x => x.Id == libraryId, cancellationToken);
        var result = new PilotRunResultDto
        {
            LibraryId = libraryId,
            MaxFiles = maxFiles,
            MaxEstimatedStagingBytes = maxEstimatedStagingBytes
        };

        if (!exists)
        {
            result.Messages.Add("Library was not found.");
            return result;
        }

        if (!request.QueueCleanup && !request.QueueTranscode)
        {
            result.Messages.Add("Pilot run skipped because both cleanup and transcode were disabled in the request.");
            return result;
        }

        var mediaItems = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId && x.PlanJson != null && x.PlanJson != "")
            .OrderBy(x => x.RelativePath)
            .Take(5000)
            .ToListAsync(cancellationToken);

        var candidates = new List<PilotCandidate>();
        foreach (var media in mediaItems)
        {
            result.Considered++;

            if (activeMediaIdSet.Contains(media.Id))
            {
                result.Skipped++;
                continue;
            }

            if (!IsPilotEligibleStatus(media.Status))
            {
                result.Skipped++;
                continue;
            }

            TranscodePlanDto? plan;
            try
            {
                plan = JsonSerializer.Deserialize<TranscodePlanDto>(media.PlanJson!, JsonOptions);
            }
            catch
            {
                result.Skipped++;
                continue;
            }

            if (plan is null || IsNoActionPlan(plan) || plan.BlockingReasons.Count > 0)
            {
                result.Skipped++;
                continue;
            }

            var jobType = IsCleanupPlan(plan) ? JobType.Cleanup : JobType.Transcode;
            if (jobType == JobType.Cleanup && !request.QueueCleanup)
            {
                result.Skipped++;
                continue;
            }

            if (jobType == JobType.Transcode && !request.QueueTranscode)
            {
                result.Skipped++;
                continue;
            }

            var estimatedOutputBytes = plan.EstimatedOutputSizeBytes ?? media.FileSizeBytes;
            var estimatedSavingBytes = plan.EstimatedRemovedBytes ?? (estimatedOutputBytes > 0 ? Math.Max(0, media.FileSizeBytes - estimatedOutputBytes) : 0);

            estimatedSavingBytes = Math.Max(0, estimatedSavingBytes);

            if (estimatedSavingBytes <= 0 || estimatedSavingBytes < minEstimatedSavingBytes)
            {
                result.Skipped++;
                continue;
            }

            var estimatedSavingRatio = media.FileSizeBytes > 0
                ? (double)estimatedSavingBytes / media.FileSizeBytes
                : 0;

            candidates.Add(new PilotCandidate(
                media.Id,
                media.RelativePath,
                jobType,
                plan.PlanKind,
                media.FileSizeBytes,
                Math.Max(0, estimatedOutputBytes),
                estimatedSavingBytes,
                estimatedSavingRatio));
        }

        var ordered = request.PreferSmallFiles
            ? candidates
                .OrderByDescending(x => x.EstimatedSavingRatio)
                .ThenByDescending(x => x.EstimatedSavingBytes)
                .ThenBy(x => x.EstimatedOutputSizeBytes)
                .ThenBy(x => x.RelativePath)
                .ToList()
            : candidates
                .OrderByDescending(x => x.EstimatedSavingBytes)
                .ThenByDescending(x => x.EstimatedSavingRatio)
                .ThenBy(x => x.EstimatedOutputSizeBytes)
                .ThenBy(x => x.RelativePath)
                .ToList();

        foreach (var candidate in ordered)
        {
            if (result.Items.Count >= maxFiles)
                break;

            if (maxEstimatedStagingBytes is not null && result.EstimatedStagingBytes + candidate.EstimatedOutputSizeBytes > maxEstimatedStagingBytes.Value)
            {
                result.Skipped++;
                continue;
            }

            var queued = await QueueExistingPlanWorkAsync(candidate.MediaId, candidate.JobType, cancellationToken);

            if (queued.AlreadyQueued)
            {
                result.AlreadyQueued++;
                result.Skipped++;
                continue;
            }

            if (!queued.Accepted || !queued.Queued)
            {
                result.Skipped++;
                if (result.Messages.Count < 10)
                    result.Messages.Add($"{candidate.RelativePath}: {queued.Message}");
                continue;
            }

            result.Queued++;

            if (queued.Queued) result.Queued++;
            if (queued.AlreadyQueued) result.AlreadyQueued++;

            result.EstimatedStagingBytes += candidate.EstimatedOutputSizeBytes;
            result.EstimatedSavingBytes += candidate.EstimatedSavingBytes;
            result.Items.Add(new PilotRunQueuedItemDto
            {
                MediaId = candidate.MediaId,
                RelativePath = candidate.RelativePath,
                JobType = candidate.JobType,
                PlanKind = candidate.PlanKind,
                InputSizeBytes = candidate.InputSizeBytes,
                EstimatedOutputSizeBytes = candidate.EstimatedOutputSizeBytes,
                EstimatedSavingBytes = candidate.EstimatedSavingBytes,
                Queued = queued.Queued,
                AlreadyQueued = queued.AlreadyQueued,
                Message = queued.Message
            });
        }

        if (result.Messages.Count == 0)
            result.Messages.Add($"Pilot run selected {result.Items.Count} file(s), queued {result.Queued} new job(s), and found {result.AlreadyQueued} already active job(s). Nothing will replace originals; outputs go to staging only.");

        return result;
    }

    private async Task<QueueLibraryWorkResultDto> QueueLibraryExistingPlanWorkAsync(int libraryId, JobType requestedJobType, CancellationToken cancellationToken)
    {
        var exists = await db.Libraries.AnyAsync(x => x.Id == libraryId, cancellationToken);
        if (!exists)
        {
            return new QueueLibraryWorkResultDto
            {
                LibraryId = libraryId,
                JobType = requestedJobType,
                Messages = ["Library was not found."]
            };
        }

        var mediaIds = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId && x.PlanJson != null && x.PlanJson != "")
            .OrderBy(x => x.RelativePath)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var result = new QueueLibraryWorkResultDto
        {
            LibraryId = libraryId,
            JobType = requestedJobType,
            Considered = mediaIds.Count
        };

        foreach (var mediaId in mediaIds)
        {
            var item = await QueueExistingPlanWorkAsync(mediaId, requestedJobType, cancellationToken);
            if (item.Queued) result.Queued++;
            else if (item.AlreadyQueued) result.AlreadyQueued++;
            else result.Skipped++;

            if (!item.Accepted && result.Messages.Count < 10)
                result.Messages.Add($"Media {mediaId}: {item.Message}");
        }

        if (result.Messages.Count == 0)
            result.Messages.Add($"Queued {result.Queued} {requestedJobType} job(s). {result.AlreadyQueued} already queued/running. {result.Skipped} skipped.");

        return result;
    }

    private async Task<QueueMediaWorkResultDto> QueueExistingPlanWorkAsync(long mediaId, JobType requestedJobType, CancellationToken cancellationToken)
    {
        var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media?.Library is null)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                Message = "Media item was not found."
            };
        }

        if (string.IsNullOrWhiteSpace(media.PlanJson))
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                MediaStatus = media.Status,
                Message = "No plan exists yet. Run Plan first."
            };
        }

        var plan = JsonSerializer.Deserialize<TranscodePlanDto>(media.PlanJson, JsonOptions);
        if (plan is null)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                MediaStatus = media.Status,
                Message = "The stored plan could not be read. Re-plan this media item."
            };
        }

        var planReviewApproved = HasApprovedPlanReview(media);
        if (plan.BlockingReasons.Count > 0 && !planReviewApproved)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                MediaStatus = media.Status,
                Message = "The plan has blocking reasons. Approve the review item before queueing work."
            };
        }

        if (IsNoActionPlan(plan))
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                JobType = requestedJobType,
                MediaStatus = media.Status,
                Message = "This plan has no cleanup/transcode work to queue."
            };
        }

        var isCleanupPlan = IsCleanupPlan(plan);
        if (requestedJobType == JobType.Cleanup && !isCleanupPlan)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                JobType = requestedJobType,
                MediaStatus = media.Status,
                Message = $"This is a {plan.PlanKind} plan, not a cleanup plan."
            };
        }

        if (requestedJobType == JobType.Transcode && isCleanupPlan)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                JobType = requestedJobType,
                MediaStatus = media.Status,
                Message = "This is a cleanup plan. Queue Cleanup instead of Transcode."
            };
        }

        var policy = DeserializePolicy(media.Library.PolicyJson);
        if (policy.Review.PlanReviewRequired && !planReviewApproved)
        {
            return new QueueMediaWorkResultDto
            {
                MediaId = mediaId,
                Accepted = false,
                Queued = false,
                JobType = requestedJobType,
                MediaStatus = media.Status,
                Message = "Plan review is required and has not approved this plan yet."
            };
        }

        var alreadyQueued = HasActiveWorkJob(media.Id, requestedJobType);
        if (!alreadyQueued)
        {
            if (requestedJobType == JobType.Cleanup)
            {
                QueueCleanupIfMissing(media, plan);
                media.Status = MediaStatus.ReadyToCleanup;
            }
            else
            {
                QueueTranscodeIfMissing(media, plan);
                media.Status = MediaStatus.ReadyToTranscode;
            }

            media.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new QueueMediaWorkResultDto
        {
            MediaId = media.Id,
            Accepted = true,
            Queued = !alreadyQueued,
            AlreadyQueued = alreadyQueued,
            JobType = requestedJobType,
            MediaStatus = media.Status,
            Message = alreadyQueued
                ? $"A {requestedJobType} job is already queued, leased, or running for this media item."
                : $"{requestedJobType} job queued. Workers will run it when processing mode allows staging work."
        };
    }

    private async Task QueueProbeIfMissingAsync(MediaItemEntity media, CancellationToken cancellationToken)
    {
        var exists = await db.Jobs.AnyAsync(x => x.MediaItemId == media.Id && x.JobType == JobType.Probe && x.Status != JobStatus.Cancelled, cancellationToken);
        if (exists)
            return;

        logger.LogInformation("Queued Probe for {Media} in library {LibraryId}", media.RelativePath, media.LibraryId);
        db.Jobs.Add(new JobEntity
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
            }, JsonOptions)
        });
        media.Status = MediaStatus.ProbeQueued;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void QueuePlanReview(MediaItemEntity media, TranscodePlanDto plan)
    {
        if (db.Jobs.Any(x => x.MediaItemId == media.Id && x.JobType == JobType.PlanReview && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running)))
            return;

        logger.LogInformation("Queued PlanReview for {Media} in library {LibraryId}", media.RelativePath, media.LibraryId);
        db.Jobs.Add(new JobEntity
        {
            JobType = JobType.PlanReview,
            Status = JobStatus.Queued,
            LibraryId = media.LibraryId,
            MediaItemId = media.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                libraryId = media.LibraryId,
                mediaId = media.Id,
                inputPath = media.FullPath,
                fileSizeBytes = media.FileSizeBytes,
                lastModifiedUtc = media.LastModifiedUtc,
                planHash = plan.PlanHash,
                planJson = plan
            }, JsonOptions)
        });
    }

    private bool HasActiveWorkJob(long mediaId, JobType jobType) =>
        db.Jobs.Any(x => x.MediaItemId == mediaId
            && x.JobType == jobType
            && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running));


    private static bool IsPilotEligibleStatus(MediaStatus status) => status is
        MediaStatus.Probed or
        MediaStatus.Planning or
        MediaStatus.NeedsReview or
        MediaStatus.ReadyToCleanup or
        MediaStatus.ReadyToTranscode;

    private static bool HasApprovedPlanReview(MediaItemEntity media)
    {
        if (string.IsNullOrWhiteSpace(media.PlanReviewJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(media.PlanReviewJson);
            var status = GetString(doc.RootElement, "reviewStatus") ?? string.Empty;
            return status.Equals("Approved", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void QueueWorkIfMissing(MediaItemEntity media, TranscodePlanDto plan)
    {
        if (IsCleanupPlan(plan))
            QueueCleanupIfMissing(media, plan);
        else
            QueueTranscodeIfMissing(media, plan);
    }


    private async Task QueueWorkIfAutoAllowedAsync(MediaItemEntity media, TranscodePlanDto? plan, CancellationToken cancellationToken)
    {
        if (plan is null || IsNoActionPlan(plan) || (plan.BlockingReasons.Count > 0 && !HasApprovedPlanReview(media)))
            return;

        var policy = media.Library is null ? null : DeserializePolicy(media.Library.PolicyJson);
        if (policy is null)
        {
            var policyJson = await db.Libraries.AsNoTracking()
                .Where(x => x.Id == media.LibraryId)
                .Select(x => x.PolicyJson)
                .FirstOrDefaultAsync(cancellationToken);
            policy = string.IsNullOrWhiteSpace(policyJson) ? new LibraryPolicyDto() : DeserializePolicy(policyJson);
        }
        policy ??= new LibraryPolicyDto();
        policy.Execution ??= new ExecutionPolicyDto();

        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
        if (execution.RequirePlanReviewBeforeAutoQueue && policy.Review.PlanReviewRequired && !HasApprovedPlanReview(media))
            return;

        if (IsCleanupPlan(plan))
        {
            if (execution.AutoQueueCleanupJobs && policy.Execution.GenerateCleanupJobs)
            {
                QueueCleanupIfMissing(media, plan);
                media.Status = MediaStatus.ReadyToCleanup;
            }
            return;
        }

        if (execution.AutoQueueTranscodeJobs && policy.Execution.GenerateTranscodeJobs)
        {
            QueueTranscodeIfMissing(media, plan);
            media.Status = MediaStatus.ReadyToTranscode;
        }
    }

    private void QueueCleanupIfMissing(MediaItemEntity media, TranscodePlanDto plan)
    {
        if (db.Jobs.Any(x => x.MediaItemId == media.Id && x.JobType == JobType.Cleanup && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running)))
            return;

        logger.LogInformation("Queued Cleanup for {Media} in library {LibraryId}", media.RelativePath, media.LibraryId);
        db.Jobs.Add(new JobEntity
        {
            JobType = JobType.Cleanup,
            Status = JobStatus.Queued,
            LibraryId = media.LibraryId,
            MediaItemId = media.Id,
            RequiredEncoder = null,
            PayloadJson = JsonSerializer.Serialize(new
            {
                libraryId = media.LibraryId,
                mediaId = media.Id,
                inputPath = media.FullPath,
                stagingOutputPath = plan.StagingOutputPath,
                planHash = plan.PlanHash,
                requiredEncoder = plan.RequiredEncoder,
                requiredEncoderEngine = plan.RequiredEncoderEngine,
                planJson = plan,
                ffmpegArgs = plan.FfmpegArgs
            }, JsonOptions)
        });
    }

    private void QueueTranscodeIfMissing(MediaItemEntity media, TranscodePlanDto plan)
    {
        if (db.Jobs.Any(x => x.MediaItemId == media.Id && x.JobType == JobType.Transcode && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running)))
            return;

        logger.LogInformation("Queued Transcode for {Media} in library {LibraryId}", media.RelativePath, media.LibraryId);
        db.Jobs.Add(new JobEntity
        {
            JobType = JobType.Transcode,
            Status = JobStatus.Queued,
            LibraryId = media.LibraryId,
            MediaItemId = media.Id,
            RequiredEncoder = plan.RequiredEncoder,
            PayloadJson = JsonSerializer.Serialize(new
            {
                libraryId = media.LibraryId,
                mediaId = media.Id,
                inputPath = media.FullPath,
                stagingOutputPath = plan.StagingOutputPath,
                planHash = plan.PlanHash,
                requiredEncoder = plan.RequiredEncoder,
                requiredEncoderEngine = plan.RequiredEncoderEngine,
                planJson = plan,
                ffmpegArgs = plan.FfmpegArgs
            }, JsonOptions)
        });
    }


    private async Task<ProfileSelection> SelectVideoProfileAsync(LibraryPolicyDto policy, CancellationToken cancellationToken)
    {
        var profiles = await db.Profiles.AsNoTracking()
            .Where(x => x.Enabled && x.TargetCodec == policy.Video.TargetCodec)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        if (profiles.Count == 0)
            return ProfileSelection.None($"No enabled profiles exist for target codec '{policy.Video.TargetCodec}'.");

        var preferred = profiles.FirstOrDefault(x => x.Name.Equals(policy.Video.Profile, StringComparison.OrdinalIgnoreCase));
        var compatible = profiles
            .Select(x => new ProfileCandidate(x, InferEncoderEngine(x.Encoder)))
            .Where(x => x.Engine is EncoderEngine.Cpu or EncoderEngine.Gpu)
            .ToList();

        var preferredCandidate = preferred is null ? null : new ProfileCandidate(preferred, InferEncoderEngine(preferred.Encoder));
        bool IsAllowed(ProfileCandidate candidate) => policy.Video.TranscodeEngine switch
        {
            TranscodeEnginePolicy.GpuOnly => candidate.Engine == EncoderEngine.Gpu,
            TranscodeEnginePolicy.CpuOnly => candidate.Engine == EncoderEngine.Cpu,
            _ => candidate.Engine is EncoderEngine.Gpu or EncoderEngine.Cpu
        };

        if (preferredCandidate is not null && IsAllowed(preferredCandidate))
        {
            return new ProfileSelection(
                preferredCandidate.Profile,
                preferredCandidate.Engine,
                $"Using preferred profile '{preferredCandidate.Profile.Name}' ({preferredCandidate.Engine}) because it is compatible with policy '{policy.Video.TranscodeEngine}'.");
        }

        var ordered = policy.Video.TranscodeEngine switch
        {
            TranscodeEnginePolicy.GpuOnly => compatible.Where(x => x.Engine == EncoderEngine.Gpu),
            TranscodeEnginePolicy.CpuOnly => compatible.Where(x => x.Engine == EncoderEngine.Cpu),
            TranscodeEnginePolicy.PreferCpu => compatible.OrderBy(x => x.Engine == EncoderEngine.Cpu ? 0 : 1).ThenBy(x => x.Profile.Name),
            TranscodeEnginePolicy.Either => compatible.OrderBy(x => x.Engine == EncoderEngine.Gpu ? 0 : 1).ThenBy(x => x.Profile.Name),
            _ => compatible.OrderBy(x => x.Engine == EncoderEngine.Gpu ? 0 : 1).ThenBy(x => x.Profile.Name)
        };

        var selected = ordered.FirstOrDefault();
        if (selected is null)
            return ProfileSelection.None($"No compatible profiles matched transcode engine policy '{policy.Video.TranscodeEngine}'. Preferred profile was '{policy.Video.Profile}'.");

        var reason = preferredCandidate is null
            ? $"Preferred profile '{policy.Video.Profile}' was not found; selected '{selected.Profile.Name}' ({selected.Engine}) for policy '{policy.Video.TranscodeEngine}'."
            : $"Preferred profile '{preferredCandidate.Profile.Name}' is {preferredCandidate.Engine}, which does not satisfy policy '{policy.Video.TranscodeEngine}'; selected '{selected.Profile.Name}' ({selected.Engine}).";

        return new ProfileSelection(selected.Profile, selected.Engine, reason);
    }

    private async Task ClearExistingPlanningReviewsAsync(long mediaId, CancellationToken cancellationToken)
    {
        var existing = await db.ReviewItems.Where(x => x.MediaItemId == mediaId && !x.Resolved && (x.ReviewType == ReviewType.PolicyAmbiguous || x.ReviewType == ReviewType.PlanReviewFailed)).ToListAsync(cancellationToken);
        foreach (var item in existing)
        {
            item.Resolved = true;
            item.ResolvedUtc = DateTime.UtcNow;
        }
    }

    private TranscodePlanDto BuildPlan(MediaItemEntity media, LibraryEntity library, LibraryPolicyDto policy, ProfileSelection profileSelection, ProbeData probe)
    {
        var warnings = new List<string>();
        var blocking = new List<string>();
        var streamPlans = new List<StreamPlanDto>();
        var cleanupCandidate = IsCleanupFirstStrategy(policy.ProcessingStrategy);
        var profile = profileSelection.Profile;

        var plan = new TranscodePlanDto
        {
            MediaId = media.Id,
            LibraryId = media.LibraryId,
            InputPath = media.FullPath,
            StagingOutputPath = BuildStagingPath(library, media),
            PlanKind = cleanupCandidate ? "CleanupOnly" : "Transcode",
            ProcessingStrategy = policy.ProcessingStrategy,
            TargetVideoCodec = cleanupCandidate ? "copy" : policy.Video.TargetCodec,
            VideoProfile = cleanupCandidate ? "stream_cleanup_copy" : profile?.Name ?? policy.Video.Profile,
            TranscodeEnginePolicy = policy.Video.TranscodeEngine,
            RequiredEncoderEngine = cleanupCandidate ? EncoderEngine.Copy : EncoderEngine.Unknown,
            RequiredEncoder = null,
            EncoderSelectionReason = profileSelection.Reason,
            InputFileSizeBytes = media.FileSizeBytes,
            CreatedUtc = DateTime.UtcNow
        };

        var videoNeedsTranscode = false;
        var video = probe.Streams.FirstOrDefault(x => x.StreamType == "video");

        if (policy.Audio.KeepOriginalLanguage && string.IsNullOrWhiteSpace(media.OriginalLanguage) && policy.Metadata.RequireOriginalLanguageWhenPolicyUsesIt)
            blocking.Add("Library policy keeps original language, but original language is unknown.");

        if (video is null)
        {
            blocking.Add("No video stream was found.");
        }
        else
        {
            var codec = NormalizeCodec(video.CodecName);
            videoNeedsTranscode = policy.Video.OnlyConvertCodecs.Select(NormalizeCodec).Contains(codec, StringComparer.OrdinalIgnoreCase);
            var shouldSkip = policy.Video.SkipCodecs.Select(NormalizeCodec).Contains(codec, StringComparer.OrdinalIgnoreCase);

            if (!cleanupCandidate)
            {
                if (videoNeedsTranscode && profile?.Encoder is not null && !profile.Encoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
                {
                    plan.RequiredEncoder = profile.Encoder;
                    plan.RequiredEncoderEngine = profileSelection.Engine == EncoderEngine.Unknown ? InferEncoderEngine(profile.Encoder) : profileSelection.Engine;
                }
                else if (!videoNeedsTranscode)
                {
                    plan.RequiredEncoderEngine = EncoderEngine.Copy;
                    plan.EncoderSelectionReason = "Video codec is configured to copy/skip, so no CPU or GPU encoder is required.";
                }

                if (!videoNeedsTranscode && !shouldSkip)
                    warnings.Add($"Video codec '{video.CodecName}' is not explicitly configured; video will be copied.");
                if (videoNeedsTranscode && profile is null)
                    blocking.Add($"No enabled video profile compatible with transcode engine policy '{policy.Video.TranscodeEngine}' and target codec '{policy.Video.TargetCodec}' was found. Preferred profile was '{policy.Video.Profile}'.");
            }

            streamPlans.Add(ToStreamPlan(
                video,
                cleanupCandidate ? "Copy" : videoNeedsTranscode ? "Transcode" : "Copy",
                cleanupCandidate ? "Cleanup/remux copies video without re-encoding." : videoNeedsTranscode ? $"{codec} matches conversion policy" : "Video codec is configured to copy/skip"));
        }

        var desiredAudioLanguages = BuildDesiredLanguages(policy.Audio.KeepLanguages, media.OriginalLanguage, policy.Audio.KeepOriginalLanguage, policy.Audio.OriginalLanguageFirst);
        var audioStreams = probe.Streams.Where(x => x.StreamType == "audio").ToList();
        var selectedAudio = SelectAudioStreams(audioStreams, desiredAudioLanguages, policy.Audio, warnings, blocking);
        var selectedAudioIds = selectedAudio.Select(x => x.Index).ToHashSet();

        foreach (var audio in audioStreams)
        {
            if (selectedAudioIds.Contains(audio.Index))
            {
                var reasons = new List<string>();
                if (IsPremiumAudio(audio, policy.Audio))
                    reasons.Add(BuildSelectedPremiumReason(audio, audioStreams, selectedAudio, policy.Audio));
                if (IsCompatibilityAudio(audio, selectedAudio, policy.Audio)) reasons.Add("compatibility fallback");
                if (string.IsNullOrWhiteSpace(audio.Language)) reasons.Add("fallback audio");
                if (reasons.Count == 0) reasons.Add("best audio stream for kept language");

                var sp = ToStreamPlan(audio, cleanupCandidate || policy.Audio.TargetCodec.Equals("copy", StringComparison.OrdinalIgnoreCase) ? "Copy" : "Transcode", string.Join("; ", reasons));
                sp.ProtectedAudio = IsPremiumAudio(audio, policy.Audio);
                sp.CompatibilityAudio = IsCompatibilityAudio(audio, selectedAudio, policy.Audio);
                streamPlans.Add(sp);
            }
            else if (IsCommentary(audio) && policy.Audio.RemoveCommentary)
            {
                streamPlans.Add(ToStreamPlan(audio, "Remove", "Suspected commentary track"));
            }
            else if (IsDescriptiveAudio(audio) && policy.Audio.RemoveDescriptiveAudio)
            {
                streamPlans.Add(ToStreamPlan(audio, "Remove", "Suspected descriptive audio track"));
            }
            else if (IsPremiumAudio(audio, policy.Audio))
            {
                streamPlans.Add(ToStreamPlan(audio, "Remove", BuildRemovedPremiumReason(audio, selectedAudio, policy.Audio)));
            }
            else if (ExceedsMaximumChannels(audio, policy.Audio))
            {
                streamPlans.Add(ToStreamPlan(audio, "Remove", $"Audio exceeds maximum channel policy ({audio.Channels ?? 0} > {policy.Audio.MaximumChannels})"));
            }
            else
            {
                streamPlans.Add(ToStreamPlan(audio, "Remove", "Duplicate or language not selected by audio policy"));
            }
        }

        var desiredSubtitleLanguages = BuildDesiredLanguages(policy.Subtitles.KeepLanguages, media.OriginalLanguage, policy.Subtitles.KeepOriginalLanguage, originalFirst: true);
        var subtitleStreams = probe.Streams.Where(x => x.StreamType == "subtitle").ToList();
        var selectedSubtitles = subtitleStreams
            .Where(x => !string.IsNullOrWhiteSpace(x.Language) && desiredSubtitleLanguages.Contains(x.Language, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Forced)
            .ThenBy(x => LanguageOrderIndex(desiredSubtitleLanguages, x.Language))
            .ThenBy(x => x.Index)
            .ToList();
        if (policy.Subtitles.KeepForced)
            selectedSubtitles.AddRange(subtitleStreams.Where(x => x.Forced && !selectedSubtitles.Any(s => s.Index == x.Index) && (string.IsNullOrWhiteSpace(x.Language) || desiredSubtitleLanguages.Contains(x.Language, StringComparer.OrdinalIgnoreCase))).OrderBy(x => x.Index));

        if (policy.Subtitles.UnknownSubtitleAction == UnknownTrackAction.NeedsReview && subtitleStreams.Any(x => string.IsNullOrWhiteSpace(x.Language)))
            warnings.Add("One or more subtitle tracks have unknown language.");

        var selectedSubtitleIds = selectedSubtitles.Select(x => x.Index).ToHashSet();
        foreach (var sub in selectedSubtitles)
            streamPlans.Add(ToStreamPlan(sub, "Copy", sub.Forced ? "Forced subtitle kept by policy" : "Language matches subtitle policy"));
        foreach (var sub in subtitleStreams.Where(x => !selectedSubtitleIds.Contains(x.Index)))
            streamPlans.Add(ToStreamPlan(sub, "Remove", "Subtitle does not match keep policy"));

        foreach (var other in probe.Streams.Where(x => x.StreamType != "video" && x.StreamType != "audio" && x.StreamType != "subtitle").OrderBy(x => x.Index))
        {
            if (other.StreamType == "attachment")
                streamPlans.Add(ToStreamPlan(other, "Copy", "Attachment stream kept by default."));
        }

        plan.Warnings = warnings;
        plan.BlockingReasons = blocking;
        plan.Streams = streamPlans;
        AssignOutputMappingAndDispositions(plan);
        ApplySavingsEstimate(plan);

        var cleanupReasons = DetermineCleanupReasons(plan);
        plan.CleanupRequired = cleanupReasons.Count > 0;
        plan.CleanupReasons = cleanupReasons;

        if (cleanupCandidate && policy.ProcessingStrategy == ProcessingStrategy.CleanupThenTranscode && !plan.CleanupRequired && videoNeedsTranscode)
        {
            ConvertCleanupCandidateToTranscodePlan(plan, policy, profileSelection, profile);
            cleanupCandidate = false;
        }
        else if (cleanupCandidate && !plan.CleanupRequired)
        {
            plan.PlanKind = "NoAction";
            plan.TargetVideoCodec = "none";
            plan.VideoProfile = "not_required";
            plan.RequiredEncoder = null;
            plan.RequiredEncoderEngine = EncoderEngine.Copy;
            plan.EncoderSelectionReason = "No cleanup/remux work is required for this file.";
            plan.SavingsNotes.Add("Cleanup skipped: all kept streams already match the policy order and audio default flags.");
        }
        else if (cleanupCandidate && plan.CleanupRequired)
        {
            if (!plan.Streams.Any(x => x.Action == "Remove"))
                plan.SavingsNotes.Add("Cleanup is required for stream ordering or default-track fixes, but no streams are removed so the space saving is expected to be 0 bytes.");
            foreach (var reason in plan.CleanupReasons)
                plan.SavingsNotes.Add($"Cleanup action: {reason}.");
        }

        plan.FfmpegArgs = IsNoActionPlan(plan) ? [] : BuildFfmpegArgs(plan, IsCleanupPlan(plan) || !videoNeedsTranscode ? null : profile);
        return plan;
    }

    private string BuildStagingPath(LibraryEntity library, MediaItemEntity media)
    {
        var libraryName = string.Join("_", library.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(libraryName)) libraryName = $"library-{library.Id}";
        return Path.Combine(storageOptions.Value.StagingRoot, libraryName, media.RelativePath);
    }

    private static List<string> BuildFfmpegArgs(TranscodePlanDto plan, ProfileEntity? profile)
    {
        var args = new List<string> { "-hide_banner", "-y", "-i", "{input}" };
        foreach (var stream in plan.Streams.Where(IsKeptOutputStream).OrderBy(x => x.OutputStreamIndex ?? int.MaxValue))
        {
            args.Add("-map");
            args.Add($"0:{stream.SourceStreamIndex}");
        }

        args.Add("-map"); args.Add("0:t?");
        args.Add("-map_chapters"); args.Add("0");
        args.Add("-map_metadata"); args.Add("0");

        var profileArgs = profile is null
            ? new List<string> { "-c:v", "copy" }
            : JsonSerializer.Deserialize<List<string>>(profile.ArgumentsJson, JsonOptions) ?? new List<string> { "-c:v", "copy" };
        args.AddRange(profileArgs);
        args.Add("-c:a"); args.Add("copy");
        args.Add("-c:s"); args.Add("copy");

        foreach (var audio in plan.Streams.Where(x => IsKeptOutputStream(x) && x.StreamType == "audio" && x.OutputTypeIndex is not null).OrderBy(x => x.OutputTypeIndex))
        {
            args.Add($"-disposition:a:{audio.OutputTypeIndex}");
            args.Add(audio.OutputDefault ? "default" : "0");
        }

        args.Add("{output}");
        return args;
    }

    private static bool IsKeptOutputStream(StreamPlanDto stream) =>
        (stream.Action == "Copy" || stream.Action == "Transcode") && stream.StreamType != "attachment";

    private static void ApplySavingsEstimate(TranscodePlanDto plan)
    {
        var removed = plan.Streams.Where(x => x.Action == "Remove").ToList();
        if (removed.Count == 0)
        {
            plan.EstimatedRemovedBytes = 0;
            plan.EstimatedOutputSizeBytes = plan.InputFileSizeBytes;
            plan.EstimatedSavingsComplete = true;
            return;
        }

        foreach (var stream in removed)
            stream.EstimatedSavingBytes = stream.EstimatedSizeBytes;

        var knownRemovedBytes = removed.Where(x => x.EstimatedSavingBytes is not null).Sum(x => x.EstimatedSavingBytes!.Value);
        var unknownCount = removed.Count(x => x.EstimatedSavingBytes is null);
        plan.EstimatedRemovedBytes = knownRemovedBytes;
        plan.EstimatedOutputSizeBytes = Math.Max(0, plan.InputFileSizeBytes - knownRemovedBytes);
        plan.EstimatedSavingsComplete = unknownCount == 0;

        if (unknownCount > 0)
            plan.SavingsNotes.Add($"{unknownCount} removed stream(s) do not expose a byte size or usable bitrate in ffprobe; actual savings may be higher than the known estimate.");

        if (knownRemovedBytes == 0 && unknownCount > 0)
            plan.SavingsNotes.Add("This container does not expose enough stream-size data to calculate a reliable saving before remuxing.");
    }

    private static void AssignOutputMappingAndDispositions(TranscodePlanDto plan)
    {
        var outputIndex = 0;
        var typeIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in plan.Streams.Where(IsKeptOutputStream))
        {
            stream.OutputStreamIndex = outputIndex++;
            typeIndexes.TryGetValue(stream.StreamType, out var typeIndex);
            stream.OutputTypeIndex = typeIndex;
            typeIndexes[stream.StreamType] = typeIndex + 1;
        }

        var firstAudio = true;
        foreach (var audio in plan.Streams.Where(x => IsKeptOutputStream(x) && x.StreamType == "audio").OrderBy(x => x.OutputTypeIndex ?? int.MaxValue))
        {
            audio.OutputDefault = firstAudio;
            firstAudio = false;
        }
    }

    private static List<string> DetermineCleanupReasons(TranscodePlanDto plan)
    {
        var reasons = new List<string>();
        var removedCount = plan.Streams.Count(x => x.Action == "Remove");
        if (removedCount > 0)
            reasons.Add($"remove {removedCount} unwanted stream(s)");

        var kept = plan.Streams.Where(IsKeptOutputStream).OrderBy(x => x.OutputStreamIndex ?? int.MaxValue).ToList();
        if (!kept.Select(x => x.SourceStreamIndex).SequenceEqual(kept.Select(x => x.SourceStreamIndex).OrderBy(x => x)))
            reasons.Add("reorder streams to match language preference");

        var keptAudio = kept.Where(x => x.StreamType == "audio").ToList();
        if (keptAudio.Any(x => x.Default != x.OutputDefault))
            reasons.Add("fix audio default-track flags");

        return reasons;
    }

    private static void ConvertCleanupCandidateToTranscodePlan(TranscodePlanDto plan, LibraryPolicyDto policy, ProfileSelection profileSelection, ProfileEntity? profile)
    {
        plan.PlanKind = "Transcode";
        plan.TargetVideoCodec = policy.Video.TargetCodec;
        plan.VideoProfile = profile?.Name ?? policy.Video.Profile;
        plan.RequiredEncoder = profile?.Encoder;
        plan.RequiredEncoderEngine = profile?.Encoder is null ? EncoderEngine.Unknown : profileSelection.Engine == EncoderEngine.Unknown ? InferEncoderEngine(profile.Encoder) : profileSelection.Engine;
        plan.EncoderSelectionReason = profileSelection.Reason + " CleanupThenTranscode skipped the cleanup stage because no cleanup/remux changes were needed.";
        if (profile is null)
            plan.BlockingReasons.Add($"No enabled video profile compatible with transcode engine policy '{policy.Video.TranscodeEngine}' and target codec '{policy.Video.TargetCodec}' was found. Preferred profile was '{policy.Video.Profile}'.");
        plan.CleanupRequired = false;
        plan.CleanupReasons = [];
        plan.SavingsNotes.Add("CleanupThenTranscode skipped cleanup: no stream removal, reorder, or default-track fix was needed. The plan proceeds directly to transcode.");

        foreach (var video in plan.Streams.Where(x => x.StreamType == "video" && x.Action == "Copy"))
        {
            video.Action = "Transcode";
            video.Reason = $"{NormalizeCodec(video.CodecName)} matches conversion policy; cleanup was skipped";
        }
    }

    private static List<ProbeStream> SelectAudioStreams(List<ProbeStream> audioStreams, List<string> desiredLanguages, AudioPolicyDto policy, List<string> warnings, List<string> blocking)
    {
        var candidates = audioStreams
            .Where(x => !IsCommentary(x) || !policy.RemoveCommentary)
            .Where(x => !IsDescriptiveAudio(x) || !policy.RemoveDescriptiveAudio)
            .Where(x => !ExceedsMaximumChannels(x, policy) || IsPremiumAudio(x, policy))
            .ToList();

        var selected = new List<ProbeStream>();

        foreach (var language in desiredLanguages)
        {
            var languageCandidates = candidates
                .Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (languageCandidates.Count == 0)
                continue;

            if (policy.ReviewDifferentMixTitles)
                AddDifferentMixWarning(language, languageCandidates, warnings, policy);

            if (policy.DuplicateMode == AudioDuplicateMode.KeepAllAllowed)
            {
                selected.AddRange(languageCandidates.OrderBy(x => AudioRank(x, policy)).ThenBy(x => x.Index));
                continue;
            }

            var premiumTracks = languageCandidates
                .Where(x => IsPremiumAudio(x, policy))
                .OrderBy(x => PremiumAudioRank(x, policy))
                .ThenByDescending(x => x.Channels ?? 0)
                .ThenBy(x => AudioRank(x, policy))
                .ThenBy(x => x.Index)
                .ToList();

            var normalTracks = languageCandidates
                .Where(x => !IsPremiumAudio(x, policy))
                .OrderBy(x => AudioRank(x, policy))
                .ThenBy(x => x.Index)
                .ToList();

            if (policy.PremiumMode == PremiumAudioMode.KeepCompatibilityOnly)
            {
                var compatibilityOnly = normalTracks
                    .OrderBy(x => CompatibilityRank(x, policy))
                    .ThenBy(x => x.Index)
                    .FirstOrDefault();

                if (compatibilityOnly is not null)
                {
                    AddUnique(selected, compatibilityOnly);
                }
                else if (premiumTracks.Count > 0)
                {
                    AddUnique(selected, premiumTracks[0]);
                    warnings.Add($"Premium audio mode is KeepCompatibilityOnly, but no compatibility track was found for language '{language}'. Keeping the best premium track to avoid dropping all audio.");
                }

                continue;
            }

            var premiumToKeep = policy.PremiumMode switch
            {
                PremiumAudioMode.KeepAllPremium => premiumTracks.Count,
                PremiumAudioMode.KeepBestTwoPremiumAndCompatibility => Math.Min(2, premiumTracks.Count),
                PremiumAudioMode.KeepBestPremiumOnly => Math.Min(1, premiumTracks.Count),
                PremiumAudioMode.KeepBestPremiumAndCompatibility => Math.Min(1, premiumTracks.Count),
                _ => Math.Min(1, premiumTracks.Count)
            };

            for (var i = 0; i < premiumToKeep; i++)
                AddUnique(selected, premiumTracks[i]);

            if (premiumTracks.Count == 0)
            {
                var bestNormal = normalTracks.FirstOrDefault();
                if (bestNormal is not null)
                    AddUnique(selected, bestNormal);
            }
            else if (policy.KeepCompatibilityTrack && policy.PremiumMode is PremiumAudioMode.KeepBestPremiumAndCompatibility or PremiumAudioMode.KeepBestTwoPremiumAndCompatibility or PremiumAudioMode.KeepAllPremium)
            {
                var compatibility = normalTracks
                    .OrderBy(x => CompatibilityRank(x, policy))
                    .ThenBy(x => x.Index)
                    .FirstOrDefault();
                if (compatibility is not null)
                    AddUnique(selected, compatibility);
            }

            if (policy.DuplicateMode == AudioDuplicateMode.KeepBestAndStereoPerLanguage)
            {
                var stereo = normalTracks
                    .Where(x => (x.Channels ?? 0) <= 2)
                    .OrderByDescending(x => x.Channels ?? 0)
                    .ThenBy(x => x.Index)
                    .FirstOrDefault();
                if (stereo is not null)
                    AddUnique(selected, stereo);
            }
        }

        if (selected.Count == 0 && policy.FallbackToDefault)
        {
            var fallback = candidates.Where(x => x.Default).OrderBy(x => AudioRank(x, policy)).FirstOrDefault();
            if (fallback is not null)
            {
                selected.Add(fallback);
                warnings.Add("Audio fallback selected the default audio stream because no language matched the policy.");
            }
        }

        if (selected.Count == 0 && policy.FallbackToFirst && candidates.Count > 0)
        {
            selected.Add(candidates.OrderBy(x => AudioRank(x, policy)).ThenBy(x => x.Index).First());
            warnings.Add("Audio fallback selected a best-effort audio stream because no language/default stream matched the policy.");
        }

        if (audioStreams.Count > 0 && selected.Count == 0)
            blocking.Add("No audio stream matched the library policy and no fallback audio was selected.");
        if (audioStreams.Count == 0)
            blocking.Add("No audio stream was found.");

        if (policy.UnknownAudioAction == UnknownTrackAction.NeedsReview && audioStreams.Count(x => string.IsNullOrWhiteSpace(x.Language)) > 1 && selected.Any(x => string.IsNullOrWhiteSpace(x.Language)))
            blocking.Add("Multiple unknown-language audio tracks exist and fallback selected an unknown track.");

        return selected
            .OrderBy(x => LanguageOrderIndex(desiredLanguages, x.Language))
            .ThenBy(x => IsPremiumAudio(x, policy) ? 0 : IsCompatibilityAudio(x, selected, policy) ? 1 : 2)
            .ThenBy(x => IsPremiumAudio(x, policy) ? PremiumAudioRank(x, policy) : AudioRank(x, policy))
            .ThenBy(x => x.Index)
            .ToList();
    }

    private static void AddUnique(List<ProbeStream> selected, ProbeStream stream)
    {
        if (!selected.Any(x => x.Index == stream.Index))
            selected.Add(stream);
    }

    private static int AudioRank(ProbeStream stream, AudioPolicyDto policy)
    {
        if (IsPremiumAudio(stream, policy)) return PremiumAudioRank(stream, policy);
        var channels = stream.Channels ?? 0;
        var preferred = Math.Max(1, policy.PreferredChannels);
        var channelDistance = channels == 0 ? 100 : Math.Abs(channels - preferred);
        var codecRank = AudioCodecRank(stream);
        var defaultBonus = stream.Default ? -1 : 0;
        return channelDistance * 100 + codecRank * 10 + defaultBonus;
    }

    private static int CompatibilityRank(ProbeStream stream, AudioPolicyDto policy)
    {
        var channels = stream.Channels ?? 0;
        var preferred = Math.Min(Math.Max(1, policy.PreferredChannels), Math.Max(1, policy.MaximumChannels));
        var distance = channels == 0 ? 100 : Math.Abs(channels - preferred);
        return distance * 100 + AudioCodecRank(stream) * 10 + (stream.Default ? -1 : 0);
    }

    private static int AudioCodecRank(ProbeStream stream)
    {
        var codec = NormalizeCodec(stream.CodecName);
        return codec switch
        {
            "eac3" => 0,
            "ac3" => 1,
            "dts" or "dca" => 2,
            "aac" => 3,
            _ => 4
        };
    }

    private static bool IsCompatibilityAudio(ProbeStream stream, IEnumerable<ProbeStream> selectedAudio, AudioPolicyDto policy)
    {
        if (IsPremiumAudio(stream, policy)) return false;
        if (!selectedAudio.Any(x => x.Index != stream.Index && string.Equals(x.Language, stream.Language, StringComparison.OrdinalIgnoreCase) && IsPremiumAudio(x, policy))) return false;
        var channels = stream.Channels ?? 0;
        return channels == 0 || channels <= Math.Max(2, policy.MaximumChannels);
    }

    private static bool ExceedsMaximumChannels(ProbeStream stream, AudioPolicyDto policy)
    {
        var max = policy.MaximumChannels;
        if (max <= 0) return false;
        return (stream.Channels ?? 0) > max;
    }

    private static bool IsProtectedAudio(ProbeStream stream, AudioPolicyDto policy) => IsPremiumAudio(stream, policy);

    private static bool IsPremiumAudio(ProbeStream stream, AudioPolicyDto policy)
    {
        if (policy.PremiumMode == PremiumAudioMode.KeepCompatibilityOnly && !policy.PreserveSpatialAudio && !policy.PreserveLosslessAudio)
            return false;

        return (policy.PreserveSpatialAudio && IsSpatialAudio(stream)) ||
               (policy.PreserveLosslessAudio && IsLosslessOrPremiumAudio(stream));
    }

    private static bool IsSpatialAudio(ProbeStream stream)
    {
        var text = AudioSearchText(stream);
        return text.Contains("atmos", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dts:x", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dtsx", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("truehd atmos", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("e-ac-3 joc", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("joc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLosslessOrPremiumAudio(ProbeStream stream)
    {
        var codec = NormalizeCodec(stream.CodecName);
        var text = AudioSearchText(stream);
        return codec is "truehd" or "flac" or "mlp" ||
               text.Contains("dts-hd ma", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dts-hd master", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("master audio", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dts:x", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dtsx", StringComparison.OrdinalIgnoreCase) ||
               (codec is "dts" or "dca" && (stream.Channels ?? 0) >= 6);
    }

    private static int PremiumAudioRank(ProbeStream stream, AudioPolicyDto policy)
    {
        var codec = NormalizeCodec(stream.CodecName);
        var text = AudioSearchText(stream);
        var hasAtmos = text.Contains("atmos", StringComparison.OrdinalIgnoreCase) || text.Contains("joc", StringComparison.OrdinalIgnoreCase);
        var hasDtsX = text.Contains("dts:x", StringComparison.OrdinalIgnoreCase) || text.Contains("dtsx", StringComparison.OrdinalIgnoreCase);
        var isTrueHd = codec is "truehd" or "mlp" || text.Contains("truehd", StringComparison.OrdinalIgnoreCase);
        var isDtsHdMa = text.Contains("dts-hd ma", StringComparison.OrdinalIgnoreCase) || text.Contains("dts-hd master", StringComparison.OrdinalIgnoreCase) || text.Contains("master audio", StringComparison.OrdinalIgnoreCase);
        var isDts = codec is "dts" or "dca" || text.Contains("dts", StringComparison.OrdinalIgnoreCase);
        var isFlac = codec == "flac";
        var isEac3 = codec == "eac3";
        var isAc3 = codec == "ac3";
        var isAac = codec == "aac";

        return policy.PremiumRanking switch
        {
            PremiumAudioRanking.PreferAtmosThenDts =>
                hasDtsX ? 0 :
                hasAtmos && isTrueHd ? 1 :
                hasAtmos ? 2 :
                isDtsHdMa ? 3 :
                isDts ? 4 :
                isTrueHd ? 5 :
                isFlac ? 6 :
                isEac3 ? 7 :
                isAc3 ? 8 :
                isAac ? 9 : 20,

            PremiumAudioRanking.PreferTrueHd =>
                isTrueHd && hasAtmos ? 0 :
                isTrueHd ? 1 :
                hasDtsX ? 2 :
                isDtsHdMa ? 3 :
                isDts ? 4 :
                isFlac ? 5 :
                isEac3 ? 6 :
                isAc3 ? 7 :
                isAac ? 8 : 20,

            PremiumAudioRanking.PreferDts =>
                hasDtsX ? 0 :
                isDtsHdMa ? 1 :
                isDts ? 2 :
                isTrueHd && hasAtmos ? 3 :
                isTrueHd ? 4 :
                isFlac ? 5 :
                isEac3 ? 6 :
                isAc3 ? 7 :
                isAac ? 8 : 20,

            _ =>
                isTrueHd && hasAtmos ? 0 :
                hasDtsX ? 1 :
                isTrueHd ? 2 :
                isDtsHdMa ? 3 :
                isFlac ? 4 :
                hasAtmos && isEac3 ? 5 :
                isDts ? 6 :
                isEac3 ? 7 :
                isAc3 ? 8 :
                isAac ? 9 : 20
        };
    }

    private static string BuildSelectedPremiumReason(ProbeStream stream, List<ProbeStream> allAudioStreams, IEnumerable<ProbeStream> selectedAudio, AudioPolicyDto policy)
    {
        var sameLanguagePremium = allAudioStreams.Count(x => string.Equals(x.Language, stream.Language, StringComparison.OrdinalIgnoreCase) && IsPremiumAudio(x, policy));
        var selectedPremium = selectedAudio.Count(x => string.Equals(x.Language, stream.Language, StringComparison.OrdinalIgnoreCase) && IsPremiumAudio(x, policy));

        if (sameLanguagePremium > selectedPremium)
            return $"selected premium track by {policy.PremiumRanking}; duplicate premium tracks will be removed";

        return "selected premium/spatial audio";
    }

    private static string BuildRemovedPremiumReason(ProbeStream stream, IEnumerable<ProbeStream> selectedAudio, AudioPolicyDto policy)
    {
        if (policy.PremiumMode == PremiumAudioMode.KeepCompatibilityOnly)
            return "Premium audio removed by compatibility-only policy";

        var selectedPremium = selectedAudio
            .Where(x => string.Equals(x.Language, stream.Language, StringComparison.OrdinalIgnoreCase) && IsPremiumAudio(x, policy))
            .OrderBy(x => PremiumAudioRank(x, policy))
            .ThenByDescending(x => x.Channels ?? 0)
            .FirstOrDefault();

        if (selectedPremium is not null)
            return $"Duplicate premium audio; selected {DescribeAudio(selectedPremium)} by {policy.PremiumMode}/{policy.PremiumRanking}";

        return "Premium audio not selected by audio policy";
    }

    private static string DescribeAudio(ProbeStream stream)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.CodecName)) parts.Add(stream.CodecName!);
        if (!string.IsNullOrWhiteSpace(stream.ChannelLayout)) parts.Add(stream.ChannelLayout!);
        else if (stream.Channels is not null) parts.Add($"{stream.Channels}ch");
        if (!string.IsNullOrWhiteSpace(stream.Title)) parts.Add($"'{stream.Title}'");
        return string.Join(' ', parts);
    }

    private static void AddDifferentMixWarning(string language, List<ProbeStream> languageCandidates, List<string> warnings, AudioPolicyDto policy)
    {
        var premiumTracks = languageCandidates.Where(x => IsPremiumAudio(x, policy)).ToList();
        if (premiumTracks.Count < 2)
            return;

        var meaningfulTitles = premiumTracks
            .Select(x => (x.Title ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (meaningfulTitles.Count <= 1)
            return;

        var mixHints = new[] { "theatrical", "original", "remix", "near field", "far field", "mono", "stereo", "imax", "extended", "director" };
        if (meaningfulTitles.Any(title => mixHints.Any(hint => title.Contains(hint, StringComparison.OrdinalIgnoreCase))))
            warnings.Add($"Multiple premium {language} audio tracks appear to have different mix titles: {string.Join(" | ", meaningfulTitles.Take(4))}. Review the plan before queueing cleanup.");
    }

    private static string AudioSearchText(ProbeStream stream) => string.Join(' ', new[]
    {
        stream.CodecName,
        stream.CodecLongName,
        stream.Profile,
        stream.ChannelLayout,
        stream.Title
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));

    private static bool IsCommentary(ProbeStream stream) => stream.Title?.Contains("commentary", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsDescriptiveAudio(ProbeStream stream)
    {
        var title = stream.Title ?? string.Empty;
        return title.Contains("descriptive", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("audio description", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("ad", StringComparison.OrdinalIgnoreCase);
    }

    private static int LanguageOrderIndex(List<string> desiredLanguages, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return int.MaxValue - 1;
        var index = desiredLanguages.FindIndex(x => string.Equals(x, language, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue - 1 : index;
    }

    private static StreamPlanDto ToStreamPlan(ProbeStream stream, string action, string reason) => new()
    {
        SourceStreamIndex = stream.Index,
        StreamType = stream.StreamType,
        CodecName = stream.CodecName,
        Language = stream.Language,
        Title = stream.Title,
        Default = stream.Default,
        Forced = stream.Forced,
        Channels = stream.Channels,
        ChannelLayout = stream.ChannelLayout,
        Action = action,
        Reason = reason,
        EstimatedSizeBytes = stream.EstimatedSizeBytes,
        EstimatedSavingBytes = action == "Remove" ? stream.EstimatedSizeBytes : null,
        SizeEstimateSource = stream.SizeEstimateSource
    };

    private static List<string> BuildDesiredLanguages(IEnumerable<string> keepLanguages, string? originalLanguage, bool keepOriginalLanguage, bool originalFirst)
    {
        var result = new List<string>();
        var normalizedOriginal = NormalizeLanguage(originalLanguage);
        if (keepOriginalLanguage && originalFirst && !string.IsNullOrWhiteSpace(normalizedOriginal))
            result.Add(normalizedOriginal);

        foreach (var language in keepLanguages.Select(NormalizeLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!))
        {
            if (!result.Contains(language, StringComparer.OrdinalIgnoreCase))
                result.Add(language);
        }

        if (keepOriginalLanguage && !originalFirst && !string.IsNullOrWhiteSpace(normalizedOriginal) && !result.Contains(normalizedOriginal, StringComparer.OrdinalIgnoreCase))
            result.Add(normalizedOriginal);

        return result;
    }

    private static bool IsCleanupPlan(TranscodePlanDto plan) => plan.PlanKind.Equals("CleanupOnly", StringComparison.OrdinalIgnoreCase);
    private static bool IsNoActionPlan(TranscodePlanDto plan) => plan.PlanKind.Equals("NoAction", StringComparison.OrdinalIgnoreCase);
    private static bool IsCleanupFirstStrategy(ProcessingStrategy strategy) => strategy is ProcessingStrategy.CleanupOnly or ProcessingStrategy.CleanupThenTranscode;

    private static EncoderEngine InferEncoderEngine(string? encoder)
    {
        var value = (encoder ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value)) return EncoderEngine.Unknown;
        if (value == "copy") return EncoderEngine.Copy;
        if (value.EndsWith("_nvenc") || value.EndsWith("_qsv") || value.EndsWith("_vaapi") || value.EndsWith("_amf") || value.EndsWith("_videotoolbox") || value.EndsWith("_v4l2m2m"))
            return EncoderEngine.Gpu;
        if (value.StartsWith("lib", StringComparison.OrdinalIgnoreCase) || value is "mpeg4" or "h264" or "hevc")
            return EncoderEngine.Cpu;
        return EncoderEngine.Unknown;
    }

    private static string ComputePlanHash(TranscodePlanDto plan)
    {
        var clone = new TranscodePlanDto
        {
            PlanVersion = plan.PlanVersion,
            PlanKind = plan.PlanKind,
            ProcessingStrategy = plan.ProcessingStrategy,
            MediaId = plan.MediaId,
            LibraryId = plan.LibraryId,
            InputPath = plan.InputPath,
            StagingOutputPath = plan.StagingOutputPath,
            TargetVideoCodec = plan.TargetVideoCodec,
            VideoProfile = plan.VideoProfile,
            TranscodeEnginePolicy = plan.TranscodeEnginePolicy,
            RequiredEncoderEngine = plan.RequiredEncoderEngine,
            RequiredEncoder = plan.RequiredEncoder,
            EncoderSelectionReason = plan.EncoderSelectionReason,
            InputFileSizeBytes = plan.InputFileSizeBytes,
            EstimatedRemovedBytes = plan.EstimatedRemovedBytes,
            EstimatedOutputSizeBytes = plan.EstimatedOutputSizeBytes,
            EstimatedSavingsComplete = plan.EstimatedSavingsComplete,
            CleanupRequired = plan.CleanupRequired,
            CleanupReasons = plan.CleanupReasons,
            SavingsNotes = plan.SavingsNotes,
            Streams = plan.Streams,
            FfmpegArgs = plan.FfmpegArgs,
            Warnings = plan.Warnings,
            BlockingReasons = plan.BlockingReasons,
            CreatedUtc = plan.CreatedUtc
        };
        var json = JsonSerializer.Serialize(clone, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto(); }
        catch { return new LibraryPolicyDto(); }
    }

    private static ProbeData ParseProbe(string probeJson)
    {
        var result = new ProbeData();
        using var doc = JsonDocument.Parse(probeJson);
        var root = doc.RootElement;
        var probeRoot = root.TryGetProperty("probeJson", out var wrapped) ? wrapped : root;

        if (probeRoot.TryGetProperty("format", out var format))
        {
            result.FormatDurationSeconds = GetDouble(format, "duration");
            result.FormatBitRate = GetLong(format, "bit_rate");
        }

        if (!probeRoot.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var stream in streams.EnumerateArray())
        {
            var type = GetString(stream, "codec_type") ?? string.Empty;
            var index = GetInt(stream, "index") ?? -1;
            if (index < 0 || string.IsNullOrWhiteSpace(type))
                continue;

            string? language = null;
            string? title = null;
            if (stream.TryGetProperty("tags", out var tags))
            {
                language = NormalizeLanguage(GetString(tags, "language"));
                title = GetString(tags, "title");
            }

            var dispositionDefault = false;
            var dispositionForced = false;
            if (stream.TryGetProperty("disposition", out var disposition))
            {
                dispositionDefault = GetInt(disposition, "default") == 1;
                dispositionForced = GetInt(disposition, "forced") == 1;
            }

            var sizeEstimate = EstimateStreamSize(stream, result.FormatDurationSeconds);

            result.Streams.Add(new ProbeStream
            {
                Index = index,
                StreamType = type,
                CodecName = GetString(stream, "codec_name"),
                CodecLongName = GetString(stream, "codec_long_name"),
                Profile = GetString(stream, "profile"),
                Channels = GetInt(stream, "channels"),
                ChannelLayout = GetString(stream, "channel_layout"),
                Language = language,
                Title = title,
                Default = dispositionDefault,
                Forced = dispositionForced,
                EstimatedSizeBytes = sizeEstimate.SizeBytes,
                SizeEstimateSource = sizeEstimate.Source
            });
        }
        return result;
    }

    private static string NormalizeCodec(string? codec) => (codec ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "h.264" or "avc" => "h264",
        "h.265" or "x265" => "hevc",
        var value => value
    };

    private static string? NormalizeLanguage(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "" or "und" or "undefined" or "unknown" => null,
            _ => LanguageNormalizer.Normalize(value)
        };
    }

    private static StreamSizeEstimate EstimateStreamSize(JsonElement stream, double? fallbackDurationSeconds)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tags.EnumerateObject())
            {
                if (property.Name.StartsWith("NUMBER_OF_BYTES", StringComparison.OrdinalIgnoreCase) && TryReadLong(property.Value, out var byteCount) && byteCount > 0)
                    return new StreamSizeEstimate(byteCount, "ffprobe tags.NUMBER_OF_BYTES");
            }
        }

        var bitRate = GetLong(stream, "bit_rate");
        var duration = GetDouble(stream, "duration") ?? fallbackDurationSeconds;
        if (bitRate is > 0 && duration is > 0)
        {
            var bytes = (long)Math.Round(bitRate.Value * duration.Value / 8d);
            if (bytes > 0)
                return new StreamSizeEstimate(bytes, "bit_rate × duration estimate");
        }

        if (stream.TryGetProperty("tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Object)
        {
            long? tagBitRate = null;
            foreach (var property in tagElement.EnumerateObject())
            {
                if (property.Name.StartsWith("BPS", StringComparison.OrdinalIgnoreCase) && TryReadLong(property.Value, out var tagBps) && tagBps > 0)
                {
                    tagBitRate = tagBps;
                    break;
                }
            }

            if (tagBitRate is > 0 && duration is > 0)
            {
                var bytes = (long)Math.Round(tagBitRate.Value * duration.Value / 8d);
                if (bytes > 0)
                    return new StreamSizeEstimate(bytes, "ffprobe tags.BPS × duration estimate");
            }
        }

        return new StreamSizeEstimate(null, null);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return TryReadLong(value, out var number) ? number : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var direct)) return direct;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static bool TryReadLong(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number)) return true;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return true;
        number = 0;
        return false;
    }


    private sealed record ProfileSelection(ProfileEntity? Profile, EncoderEngine Engine, string Reason)
    {
        public static ProfileSelection None(string reason) => new(null, EncoderEngine.Unknown, reason);
        public static ProfileSelection Copy(string reason) => new(null, EncoderEngine.Copy, reason);
    }

    private sealed record ProfileCandidate(ProfileEntity Profile, EncoderEngine Engine);

    private sealed record PilotCandidate(
        long MediaId,
        string RelativePath,
        JobType JobType,
        string PlanKind,
        long InputSizeBytes,
        long EstimatedOutputSizeBytes,
        long EstimatedSavingBytes,
        double EstimatedSavingRatio);

    private sealed class ProbeData
    {
        public List<ProbeStream> Streams { get; } = [];
        public double? FormatDurationSeconds { get; set; }
        public long? FormatBitRate { get; set; }
    }

    private sealed class ProbeStream
    {
        public int Index { get; init; }
        public string StreamType { get; init; } = string.Empty;
        public string? CodecName { get; init; }
        public string? CodecLongName { get; init; }
        public string? Profile { get; init; }
        public int? Channels { get; init; }
        public string? ChannelLayout { get; init; }
        public string? Language { get; init; }
        public string? Title { get; init; }
        public bool Default { get; init; }
        public bool Forced { get; init; }
        public long? EstimatedSizeBytes { get; init; }
        public string? SizeEstimateSource { get; init; }
    }

    private sealed record StreamSizeEstimate(long? SizeBytes, string? Source);
}
