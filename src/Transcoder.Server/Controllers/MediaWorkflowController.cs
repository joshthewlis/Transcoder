using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/media/workflow")]
public sealed class MediaWorkflowController(TranscoderDbContext db, TranscodePlanService planner) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpPost("replan-folder")]
    public async Task<ActionResult<BulkReplanResultDto>> ReplanFolder(BulkFolderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId <= 0) return BadRequest(new { message = "libraryId is required." });
        var ids = await QueryFolderMedia(request.LibraryId, request.Path, request.IncludeFinal).ToListAsync(cancellationToken);
        return Ok(await ReplanIds(ids, request.QueueAfterReplan, request.JobType, cancellationToken));
    }

    [HttpPost("replan-library/{libraryId:int}")]
    public async Task<ActionResult<BulkReplanResultDto>> ReplanLibrary(int libraryId, [FromQuery] bool includeFinal = false, [FromQuery] bool queueAfterReplan = false, [FromQuery] JobType? jobType = null, CancellationToken cancellationToken = default)
    {
        var ids = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId)
            .Where(x => includeFinal || !FinalStatuses.Contains(x.Status))
            .OrderBy(x => x.RelativePath)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return Ok(await ReplanIds(ids, queueAfterReplan, jobType, cancellationToken));
    }

    [HttpPost("queue-folder")]
    public async Task<ActionResult<FolderQueueResultDto>> QueueFolder(FolderQueueRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId <= 0) return BadRequest(new { message = "libraryId is required." });
        if (request.JobType is not (JobType.Cleanup or JobType.Transcode)) return BadRequest(new { message = "jobType must be Cleanup or Transcode." });

        // Transcode can legitimately follow a completed/replaced cleanup phase, so final
        // cleanup statuses must remain eligible for reconsideration.
        var ids = await QueryFolderMedia(
                request.LibraryId,
                request.Path,
                includeFinal: request.JobType == JobType.Transcode)
            .ToListAsync(cancellationToken);

        var result = new FolderQueueResultDto
        {
            LibraryId = request.LibraryId,
            Path = NormalizeBrowserPath(request.Path),
            JobType = request.JobType,
            Considered = ids.Count
        };

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queueResult = request.JobType == JobType.Cleanup
                ? await planner.QueueCleanupFromExistingPlanAsync(id, cancellationToken)
                : await planner.QueueTranscodeFromExistingPlanAsync(id, cancellationToken);

            if (!queueResult.Accepted && request.ReplanWhenBlocked)
            {
                result.Replanned++;
                var prepared = await PrepareOneForReplan(id, request.JobType, queueAfterReplan: true, cancellationToken: cancellationToken);
                if (!prepared.SkippedRunning && !prepared.ProbeQueued)
                {
                    var plan = await planner.BuildAndQueuePlanForRequestedWorkAsync(
                        id,
                        request.JobType,
                        force: true,
                        cancellationToken: cancellationToken);
                    if (plan is not null)
                    {
                        queueResult = request.JobType == JobType.Cleanup
                            ? await planner.QueueCleanupFromExistingPlanAsync(id, cancellationToken)
                            : await planner.QueueTranscodeFromExistingPlanAsync(id, cancellationToken);
                    }
                }
                else if (prepared.ProbeQueued)
                {
                    // The probe payload carries the requested work intent. Probe completion will
                    // generate the plan, survive PlanReview if required, and then queue the work.
                    queueResult = new QueueMediaWorkResultDto
                    {
                        MediaId = id,
                        Accepted = true,
                        Queued = false,
                        AlreadyQueued = false,
                        JobType = request.JobType,
                        Message = "Probe queued; requested work will be queued after planning/review."
                    };
                }
            }

            if (queueResult.Queued) result.Queued++;
            else if (queueResult.AlreadyQueued) result.AlreadyQueued++;
            else
            {
                result.Skipped++;
                if (result.Messages.Count < 20)
                    result.Messages.Add($"Media {id}: {queueResult.Message}");
            }
        }

        if (result.Messages.Count == 0)
            result.Messages.Add($"Queued {result.Queued} {request.JobType} job(s). Already queued/running: {result.AlreadyQueued}. Skipped: {result.Skipped}. Replanned: {result.Replanned}.");

        return Ok(result);
    }

    [HttpPost("reviews/repair-limbo")]
    public async Task<ActionResult<ReviewRepairResultDto>> RepairLimboReviews(CancellationToken cancellationToken = default)
    {
        var items = await db.MediaItems
            .Where(x => x.PlanJson != null && x.PlanJson != "")
            .Where(x => x.Status == MediaStatus.NeedsReview || x.PlanReviewJson == null || x.PlanReviewJson == "")
            .OrderBy(x => x.RelativePath)
            .Take(5000)
            .ToListAsync(cancellationToken);

        var result = new ReviewRepairResultDto();
        foreach (var media in items)
        {
            if (await db.ReviewItems.AnyAsync(x => x.MediaItemId == media.Id && !x.Resolved, cancellationToken))
                continue;

            if (!LooksLikeReviewRequired(media))
                continue;

            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = ReviewType.ManualApprovalRequired,
                Severity = ReviewSeverity.Blocking,
                Reason = "Plan requires approval but no visible review item existed.",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    source = "repair-limbo",
                    media.PlanHash,
                    plan = SafeJsonElement(media.PlanJson),
                    planReview = SafeJsonElement(media.PlanReviewJson)
                }, JsonOptions)
            });
            media.Status = MediaStatus.NeedsReview;
            media.UpdatedUtc = DateTime.UtcNow;
            result.Created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        result.Messages.Add($"Created {result.Created} missing review item(s).");
        return Ok(result);
    }

    [HttpPost("reviews/approve-all")]
    public async Task<ActionResult<ApproveAllReviewsResultDto>> ApproveAllReviews([FromQuery] bool includeLimbo = true, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var result = new ApproveAllReviewsResultDto();
        var requestedQueues = new List<(long MediaId, JobType JobType)>();

        var reviewMediaIds = await db.ReviewItems
            .Where(x => !x.Resolved)
            .Select(x => x.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var openReviews = await db.ReviewItems.Where(x => !x.Resolved).ToListAsync(cancellationToken);
        foreach (var review in openReviews)
        {
            review.Resolved = true;
            review.ResolvedUtc = now;
            result.ResolvedReviewItems++;
        }

        if (includeLimbo)
        {
            var limboIds = await db.MediaItems.AsNoTracking()
                .Where(x => x.PlanJson != null && x.PlanJson != "")
                .Where(x => x.Status == MediaStatus.NeedsReview || x.PlanReviewJson == null || x.PlanReviewJson == "" || x.PlanReviewJson.Contains("Rejected"))
                .Select(x => x.Id)
                .Take(5000)
                .ToListAsync(cancellationToken);
            reviewMediaIds.AddRange(limboIds);
        }

        var distinctIds = reviewMediaIds.Distinct().ToList();
        var medias = await db.MediaItems.Where(x => distinctIds.Contains(x.Id)).ToListAsync(cancellationToken);
        foreach (var media in medias)
        {
            media.PlanReviewJson = JsonSerializer.Serialize(new
            {
                reviewStatus = "Approved",
                messages = Array.Empty<string>(),
                approvedUtc = now,
                approvalSource = "bulk-approve-all"
            }, JsonOptions);
            media.PlanReviewedUtc = now;
            media.UpdatedUtc = now;

            var planKind = TryReadPlanKind(media.PlanJson);
            media.Status = planKind switch
            {
                "NoAction" => MediaStatus.Skipped,
                "CleanupOnly" => MediaStatus.ReadyToCleanup,
                "Transcode" => MediaStatus.ReadyToTranscode,
                _ => media.Status == MediaStatus.NeedsReview ? MediaStatus.Probed : media.Status
            };

            var currentHistory = await db.MediaPlanHistories
                .FirstOrDefaultAsync(x => x.MediaItemId == media.Id && x.IsCurrent, cancellationToken);
            if (currentHistory is not null)
            {
                currentHistory.PlanReviewJson = media.PlanReviewJson;
                currentHistory.PlanReviewedUtc = now;
            }

            var reviewJob = await db.Jobs
                .Where(x => x.MediaItemId == media.Id
                    && x.JobType == JobType.PlanReview
                    && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (reviewJob is not null)
            {
                var requested = ReadRequestedWorkIntent(reviewJob.PayloadJson, "queueAfterReviewJobType");
                if (requested is JobType.Cleanup or JobType.Transcode)
                    requestedQueues.Add((media.Id, requested.Value));

                if (reviewJob.Status == JobStatus.Queued)
                {
                    reviewJob.Status = JobStatus.Cancelled;
                    reviewJob.LastMessage = "Cancelled because the plan was approved manually.";
                    reviewJob.CompletedUtc = now;
                }
            }

            result.ApprovedMediaItems++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var continued = 0;
        foreach (var requested in requestedQueues.Distinct())
        {
            var queued = requested.JobType == JobType.Cleanup
                ? await planner.QueueCleanupFromExistingPlanAsync(requested.MediaId, cancellationToken)
                : await planner.QueueTranscodeFromExistingPlanAsync(requested.MediaId, cancellationToken);

            if (queued.Queued || queued.AlreadyQueued)
                continued++;
        }

        result.Messages.Add(
            $"Approved {result.ApprovedMediaItems} media item(s) and resolved {result.ResolvedReviewItems} visible review row(s). " +
            $"Continued {continued} requested cleanup/transcode workflow(s).");
        return Ok(result);
    }

    private async Task<BulkReplanResultDto> ReplanIds(List<long> ids, bool queueAfterReplan, JobType? jobType, CancellationToken cancellationToken)
    {
        var result = new BulkReplanResultDto { Considered = ids.Count };
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prepared = await PrepareOneForReplan(id, jobType, queueAfterReplan, cancellationToken);
            result.CancelledJobs += prepared.CancelledJobs;
            result.ResolvedReviews += prepared.ResolvedReviews;
            if (prepared.SkippedRunning)
            {
                result.SkippedRunning++;
                continue;
            }

            if (prepared.ProbeQueued)
            {
                // Intent is stored on the Probe job and will continue automatically after probing.
                result.ProbeQueued++;
                continue;
            }

            TranscodePlanDto? plan;
            if (queueAfterReplan && jobType is JobType.Cleanup or JobType.Transcode)
            {
                plan = await planner.BuildAndQueuePlanForRequestedWorkAsync(
                    id,
                    jobType.Value,
                    force: true,
                    cancellationToken: cancellationToken);
            }
            else
            {
                plan = await planner.BuildAndQueuePlanAsync(id, force: true, cancellationToken);
            }

            if (plan is null)
            {
                result.Skipped++;
                continue;
            }

            result.Planned++;
            if (plan.BlockingReasons.Count > 0)
            {
                result.NeedsReview++;
                continue;
            }

            if (!queueAfterReplan)
                continue;

            var wanted = jobType ?? (IsCleanupPlan(plan) ? JobType.Cleanup : JobType.Transcode);
            var queued = wanted == JobType.Cleanup
                ? await planner.QueueCleanupFromExistingPlanAsync(id, cancellationToken)
                : await planner.QueueTranscodeFromExistingPlanAsync(id, cancellationToken);

            if (queued.Queued) result.Queued++;
            else if (queued.AlreadyQueued) result.AlreadyQueued++;
            // If PlanReview is required, BuildAndQueuePlanForRequestedWorkAsync stores the
            // requested work on the review job. It will be queued after approval.
        }

        result.Messages.Add(
            $"Recalculate finished. Considered {result.Considered}; planned {result.Planned}; " +
            $"probe queued {result.ProbeQueued}; needs review {result.NeedsReview}; " +
            $"skipped running {result.SkippedRunning}; queued now {result.Queued}; already queued {result.AlreadyQueued}. " +
            "Completed cleanup/transcode savings and processing history were preserved.");
        return result;
    }

    private async Task<ResetOneResult> PrepareOneForReplan(
        long mediaId,
        JobType? requestedJobType,
        bool queueAfterReplan,
        CancellationToken cancellationToken)
    {
        var result = new ResetOneResult();
        var item = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (item is null)
        {
            result.SkippedRunning = true;
            return result;
        }

        var activeJobs = await db.Jobs
            .Where(x => x.MediaItemId == mediaId
                && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .ToListAsync(cancellationToken);

        if (activeJobs.Any(x => x.Status is JobStatus.Leased or JobStatus.Running))
        {
            result.SkippedRunning = true;
            return result;
        }

        // Recalculation invalidates queued future work, but never completed work.
        foreach (var job in activeJobs.Where(x => x.Status == JobStatus.Queued
            && x.JobType is JobType.PlanReview or JobType.Cleanup or JobType.Transcode))
        {
            job.Status = JobStatus.Cancelled;
            job.LastMessage = "Cancelled because the plan was recalculated.";
            job.LastError = null;
            job.LeaseId = null;
            job.LastLeaseId = null;
            job.LeasedByWorkerId = null;
            job.LeasedByWorkerInstanceId = null;
            job.LeaseStartedUtc = null;
            job.LeaseLastSeenUtc = null;
            job.LeaseExpiresUtc = null;
            result.CancelledJobs++;
        }

        var reviews = await db.ReviewItems
            .Where(x => x.MediaItemId == mediaId && !x.Resolved)
            .ToListAsync(cancellationToken);

        foreach (var review in reviews)
        {
            review.Resolved = true;
            review.ResolvedUtc = DateTime.UtcNow;
            result.ResolvedReviews++;
        }

        // Facts are immutable:
        // - MetadataJson contains completed cleanup/transcode savings/history.
        // - ProbeJson describes the current file.
        // - PlanJson is intentionally left in place so TranscodePlanService can archive it
        //   as the previous plan revision before writing the new current plan.
        item.UpdatedUtc = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(item.ProbeJson))
        {
            item.Status = MediaStatus.ProbeQueued;
            var existingProbe = activeJobs.FirstOrDefault(x =>
                x.JobType == JobType.Probe &&
                (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running));

            if (existingProbe is null)
            {
                db.Jobs.Add(new JobEntity
                {
                    JobType = JobType.Probe,
                    Status = JobStatus.Queued,
                    LibraryId = item.LibraryId,
                    MediaItemId = item.Id,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        libraryId = item.LibraryId,
                        mediaId = item.Id,
                        inputPath = item.FullPath,
                        fileSizeBytes = item.FileSizeBytes,
                        lastModifiedUtc = item.LastModifiedUtc,
                        queueAfterPlanJobType = queueAfterReplan ? requestedJobType?.ToString() : null
                    }, JsonOptions)
                });
            }
            else if (queueAfterReplan && requestedJobType is JobType.Cleanup or JobType.Transcode)
            {
                existingProbe.PayloadJson = MergeQueueIntent(existingProbe.PayloadJson, requestedJobType.Value);
            }

            result.ProbeQueued = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static string MergeQueueIntent(string? payloadJson, JobType requestedJobType)
    {
        Dictionary<string, object?> payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(payloadJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson, JsonOptions)
                    ?? new Dictionary<string, object?>();
        }
        catch
        {
            payload = new Dictionary<string, object?>();
        }

        payload["queueAfterPlanJobType"] = requestedJobType.ToString();
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private IQueryable<long> QueryFolderMedia(int libraryId, string? path, bool includeFinal)
    {
        var currentPath = NormalizeBrowserPath(path);
        var prefix = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : currentPath + "/";
        return db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId)
            .Where(x => includeFinal || !FinalStatuses.Contains(x.Status))
            .Where(x => string.IsNullOrWhiteSpace(currentPath)
                || x.RelativePath == currentPath
                || x.RelativePath.StartsWith(prefix)
                || x.RelativePath.Replace("\\", "/") == currentPath
                || x.RelativePath.Replace("\\", "/").StartsWith(prefix))
            .OrderBy(x => x.RelativePath)
            .Select(x => x.Id);
    }

    private static readonly MediaStatus[] FinalStatuses =
    [
        MediaStatus.Skipped,
        MediaStatus.Staged,
        MediaStatus.StagedCleaned,
        MediaStatus.ReplacedCleaned,
        MediaStatus.ReplacedTranscoded,
        MediaStatus.ReplaceFailed
    ];

    private static string NormalizeBrowserPath(string? value) =>
        string.Join('/', (value ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static bool LooksLikeReviewRequired(MediaItemEntity media)
    {
        if (media.Status == MediaStatus.NeedsReview)
            return true;
        if (!string.IsNullOrWhiteSpace(media.PlanReviewJson) && media.PlanReviewJson.Contains("Rejected", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(media.PlanJson) && media.PlanJson.Contains("blockingReasons", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
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

    private static string? TryReadPlanKind(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            if (doc.RootElement.TryGetProperty("planKind", out var value))
                return value.GetString();
            if (doc.RootElement.TryGetProperty("PlanKind", out value))
                return value.GetString();
        }
        catch { }
        return null;
    }

    private static bool IsCleanupPlan(TranscodePlanDto plan) =>
        plan.PlanKind.Equals("CleanupOnly", StringComparison.OrdinalIgnoreCase);

    private static JsonElement? SafeJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return null; }
    }

    private sealed class ResetOneResult
    {
        public bool SkippedRunning { get; set; }
        public bool ProbeQueued { get; set; }
        public int CancelledJobs { get; set; }
        public int ResolvedReviews { get; set; }
    }
}

public sealed class BulkFolderRequest
{
    public int LibraryId { get; set; }
    public string? Path { get; set; }
    public bool IncludeFinal { get; set; }
    public bool QueueAfterReplan { get; set; }
    public JobType? JobType { get; set; }
}

public sealed class FolderQueueRequest
{
    public int LibraryId { get; set; }
    public string? Path { get; set; }
    public JobType JobType { get; set; } = JobType.Cleanup;
    public bool ReplanWhenBlocked { get; set; } = true;
}

public sealed class BulkReplanResultDto
{
    public int Considered { get; set; }
    public int Planned { get; set; }
    public int ProbeQueued { get; set; }
    public int NeedsReview { get; set; }
    public int Skipped { get; set; }
    public int SkippedRunning { get; set; }
    public int CancelledJobs { get; set; }
    public int ResolvedReviews { get; set; }
    public int Queued { get; set; }
    public int AlreadyQueued { get; set; }
    public List<string> Messages { get; set; } = [];
}

public sealed class FolderQueueResultDto
{
    public int LibraryId { get; set; }
    public string Path { get; set; } = string.Empty;
    public JobType JobType { get; set; }
    public int Considered { get; set; }
    public int Queued { get; set; }
    public int AlreadyQueued { get; set; }
    public int Skipped { get; set; }
    public int Replanned { get; set; }
    public List<string> Messages { get; set; } = [];
}

public sealed class ReviewRepairResultDto
{
    public int Created { get; set; }
    public List<string> Messages { get; set; } = [];
}

public sealed class ApproveAllReviewsResultDto
{
    public int ResolvedReviewItems { get; set; }
    public int ApprovedMediaItems { get; set; }
    public List<string> Messages { get; set; } = [];
}
