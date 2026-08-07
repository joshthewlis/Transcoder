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
[Route("api/media")]
public sealed class MediaController(TranscoderDbContext db, TranscodePlanService planner, MetadataRefreshService metadataRefresh, ReplacementService replacement) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<MediaItemDto>>> List([FromQuery] int? libraryId, [FromQuery] MediaStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var query = db.MediaItems.AsNoTracking();
        if (libraryId is not null) query = query.Where(x => x.LibraryId == libraryId.Value);
        if (status is not null) query = query.Where(x => x.Status == status.Value);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.RelativePath).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResultDto<MediaItemDto> { Items = items.Select(ToDto).ToList(), Page = page, PageSize = pageSize, TotalCount = total };
    }


    [HttpGet("browser")]
    public async Task<ActionResult<MediaBrowserDto>> Browser([FromQuery] int libraryId, [FromQuery] string? path = null, CancellationToken cancellationToken = default)
    {
        var exists = await db.Libraries.AsNoTracking().AnyAsync(x => x.Id == libraryId, cancellationToken);
        if (!exists) return NotFound();

        var currentPath = NormalizeBrowserPath(path);
        var prefix = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : currentPath + "/";
        var allLibraryItems = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId)
            .OrderBy(x => x.RelativePath)
            .ToListAsync(cancellationToken);

        var underCurrent = allLibraryItems
            .Where(x => string.IsNullOrWhiteSpace(currentPath) || NormalizeBrowserPath(x.RelativePath).Equals(currentPath, StringComparison.OrdinalIgnoreCase) || NormalizeBrowserPath(x.RelativePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var directFiles = new List<MediaItemEntity>();
        var directories = new Dictionary<string, List<MediaItemEntity>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in underCurrent)
        {
            var relative = NormalizeBrowserPath(item.RelativePath);
            var remainder = string.IsNullOrWhiteSpace(currentPath)
                ? relative
                : relative.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ? string.Empty : relative[prefix.Length..];

            var slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                directFiles.Add(item);
                continue;
            }

            var name = remainder[..slash];
            var childPath = string.IsNullOrWhiteSpace(currentPath) ? name : currentPath + "/" + name;
            if (!directories.TryGetValue(childPath, out var items))
            {
                items = [];
                directories[childPath] = items;
            }
            items.Add(item);
        }

        return new MediaBrowserDto
        {
            LibraryId = libraryId,
            CurrentPath = currentPath,
            ParentPath = ParentPath(currentPath),
            Breadcrumbs = BuildBreadcrumbs(currentPath),
            Totals = MediaProcessingStatsStore.BuildTotals(underCurrent),
            Directories = directories
                .OrderBy(x => x.Key)
                .Select(x => new MediaBrowserDirectoryDto
                {
                    Name = x.Key.Split('/').Last(),
                    Path = x.Key,
                    Totals = MediaProcessingStatsStore.BuildTotals(x.Value)
                })
                .ToList(),
            Items = directFiles.OrderBy(x => x.RelativePath).Select(ToDto).ToList()
        };
    }

    [HttpGet("{mediaId:long}")]
    public async Task<ActionResult<MediaItemDto>> Get(long mediaId, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        return item is null ? NotFound() : ToDto(item);
    }


    [HttpPost("{mediaId:long}/metadata")]
    public async Task<IActionResult> SetMetadata(long mediaId, SetMediaMetadataRequest request, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (item is null) return NotFound();
        item.OriginalLanguage = LanguageNormalizer.Normalize(request.OriginalLanguage);
        item.OriginalLanguageSource = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? MetadataSource.None : request.OriginalLanguageSource;
        item.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{mediaId:long}/metadata/refresh")]
    public async Task<ActionResult<MetadataRefreshResultDto>> RefreshMetadata(long mediaId, [FromQuery] bool force = true, CancellationToken cancellationToken = default)
    {
        return await metadataRefresh.RefreshMediaAsync(mediaId, force, cancellationToken);
    }

    [HttpPost("{mediaId:long}/probe")]
    public async Task<IActionResult> Probe(long mediaId, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (item is null) return NotFound();

        db.Jobs.Add(new JobEntity
        {
            JobType = JobType.Probe,
            Status = JobStatus.Queued,
            LibraryId = item.LibraryId,
            MediaItemId = item.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                libraryId = item.LibraryId,
                mediaId = item.Id,
                inputPath = item.FullPath,
                fileSizeBytes = item.FileSizeBytes,
                lastModifiedUtc = item.LastModifiedUtc
            })
        });
        item.Status = MediaStatus.ProbeQueued;
        await db.SaveChangesAsync(cancellationToken);
        return Accepted();
    }

    [HttpPost("{mediaId:long}/plan")]
    public async Task<IActionResult> Plan(long mediaId, [FromQuery] bool force = true, CancellationToken cancellationToken = default)
    {
        var plan = await planner.BuildAndQueuePlanAsync(mediaId, force, cancellationToken);
        return plan is null ? Accepted(new { mediaId, message = "Probe is required before planning." }) : Ok(plan);
    }

    [HttpGet("{mediaId:long}/plan")]
    public async Task<IActionResult> GetPlan(long mediaId, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (item is null) return NotFound();
        return string.IsNullOrWhiteSpace(item.PlanJson) ? NotFound(new { message = "No plan exists for this media item." }) : Content(item.PlanJson, "application/json");
    }


    [HttpPost("{mediaId:long}/reset-replan")]
    public async Task<ActionResult<ResetReplanMediaResultDto>> ResetAndReplan(long mediaId, CancellationToken cancellationToken = default)
    {
        // Backwards-compatible route: this is now a SAFE plan recalculation.
        // Completed processing facts (MetadataJson), probe data, and staging history are never reset here.
        var item = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (item is null) return NotFound();

        var result = new ResetReplanMediaResultDto
        {
            MediaId = item.Id,
            Reset = false,
            MediaStatus = item.Status
        };

        var activeJobs = await db.Jobs
            .Where(x => x.MediaItemId == item.Id
                && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var runningJobs = activeJobs.Where(x => x.Status is JobStatus.Leased or JobStatus.Running).ToList();
        if (runningJobs.Count > 0)
        {
            result.Message = $"Media has {runningJobs.Count} leased/running job(s). Recalculation was skipped so active work is not invalidated.";
            return Conflict(result);
        }

        // Only future/stale work is cancelled. Completed jobs and their result ledger remain immutable.
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
            result.CancelledQueuedJobs++;
        }

        var openReviews = await db.ReviewItems
            .Where(x => x.MediaItemId == item.Id && !x.Resolved)
            .ToListAsync(cancellationToken);

        foreach (var review in openReviews)
        {
            review.Resolved = true;
            review.ResolvedUtc = DateTime.UtcNow;
            result.ResolvedReviewItems++;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Do NOT clear PlanJson first: the planner archives the previous revision before replacing it.
        // Do NOT clear MetadataJson: it contains completed cleanup/transcode savings and history.
        var plan = await planner.BuildAndQueuePlanAsync(mediaId, force: true, cancellationToken);

        if (plan is null)
        {
            result.ProbeQueued = true;
            var refreshedWithoutPlan = await db.MediaItems.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
            result.MediaStatus = refreshedWithoutPlan?.Status;
            result.Message = "Probe data is required. A probe has been queued; the plan will be generated when probing completes.";
            return Accepted(result);
        }

        result.PlanGenerated = true;
        var refreshed = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        result.MediaStatus = refreshed?.Status;
        result.Message = plan.BlockingReasons.Count > 0
            ? "Plan recalculated. Completed savings/history were preserved; blocking reasons were added to Review."
            : "Plan recalculated. Completed savings/history were preserved.";

        return Ok(result);
    }

    [HttpPost("{mediaId:long}/cleanup")]
    public async Task<ActionResult<QueueMediaWorkResultDto>> Cleanup(long mediaId, CancellationToken cancellationToken)
    {
        var result = await planner.QueueCleanupFromExistingPlanAsync(mediaId, cancellationToken);
        return result.Accepted ? Accepted(result) : BadRequest(result);
    }

    [HttpPost("{mediaId:long}/transcode")]
    public async Task<ActionResult<QueueMediaWorkResultDto>> Transcode(long mediaId, CancellationToken cancellationToken)
    {
        var result = await planner.QueueTranscodeFromExistingPlanAsync(mediaId, cancellationToken);
        return result.Accepted ? Accepted(result) : BadRequest(result);
    }

    [HttpPost("{mediaId:long}/replace")]
    public async Task<ActionResult<ReplaceMediaResultDto>> Replace(long mediaId, CancellationToken cancellationToken)
    {
        var result = await replacement.ReplaceMediaAsync(mediaId, cancellationToken);
        return result.Replaced ? Ok(result) : BadRequest(result);
    }

    [HttpPost("folder/queue")]
    public async Task<ActionResult<QueueMediaFolderWorkResultDto>> QueueFolderWork(QueueMediaFolderWorkRequest request, CancellationToken cancellationToken)
    {
        if (request.LibraryId <= 0)
            return BadRequest(new { message = "LibraryId is required." });
        if (!request.QueueCleanup && !request.QueueTranscode)
            return BadRequest(new { message = "QueueCleanup or QueueTranscode must be enabled." });

        var exists = await db.Libraries.AsNoTracking().AnyAsync(x => x.Id == request.LibraryId, cancellationToken);
        if (!exists) return NotFound(new { message = "Library was not found." });

        var currentPath = NormalizeBrowserPath(request.Path);
        var mediaItems = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == request.LibraryId && x.PlanJson != null && x.PlanJson != "")
            .OrderBy(x => x.RelativePath)
            .ToListAsync(cancellationToken);

        mediaItems = mediaItems.Where(x => IsUnderBrowserPath(x.RelativePath, currentPath)).ToList();

        var result = new QueueMediaFolderWorkResultDto
        {
            LibraryId = request.LibraryId,
            Path = currentPath,
            Priority = request.Priority,
            Considered = mediaItems.Count
        };

        foreach (var media in mediaItems)
        {
            var planKind = MediaProcessingStatsStore.ReadPlanSummary(media.PlanJson).PlanKind ?? string.Empty;
            var tried = false;

            if (request.QueueCleanup && planKind.Equals("CleanupOnly", StringComparison.OrdinalIgnoreCase))
            {
                tried = true;
                var queued = await planner.QueueCleanupFromExistingPlanAsync(media.Id, cancellationToken);
                if (queued.Queued) result.QueuedCleanup++;
                else if (queued.AlreadyQueued) result.AlreadyQueued++;
                else result.Skipped++;

                if (queued.Accepted)
                    result.PrioritizedQueuedJobs += await SetQueuedJobPriorityForMediaAsync(media.Id, [JobType.Cleanup], request.Priority, cancellationToken);
                else if (result.Messages.Count < 12)
                    result.Messages.Add($"{media.RelativePath}: {queued.Message}");
            }

            if (request.QueueTranscode && planKind.Equals("Transcode", StringComparison.OrdinalIgnoreCase))
            {
                tried = true;
                var queued = await planner.QueueTranscodeFromExistingPlanAsync(media.Id, cancellationToken);
                if (queued.Queued) result.QueuedTranscode++;
                else if (queued.AlreadyQueued) result.AlreadyQueued++;
                else result.Skipped++;

                if (queued.Accepted)
                    result.PrioritizedQueuedJobs += await SetQueuedJobPriorityForMediaAsync(media.Id, [JobType.Transcode], request.Priority, cancellationToken);
                else if (result.Messages.Count < 12)
                    result.Messages.Add($"{media.RelativePath}: {queued.Message}");
            }

            if (!tried)
            {
                result.Skipped++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(media.PlanJson))
                result.EstimatedCleanupSavingsBytes += Math.Max(0, MediaProcessingStatsStore.ReadPlanSummary(media.PlanJson).EstimatedRemovedBytes ?? 0);
        }

        if (result.Messages.Count == 0)
        {
            var scope = string.IsNullOrWhiteSpace(currentPath) ? "selected library" : currentPath;
            result.Messages.Add($"Queued folder work for {scope}. Cleanup queued: {result.QueuedCleanup}. Transcode queued: {result.QueuedTranscode}. Already queued: {result.AlreadyQueued}. Priority updates: {result.PrioritizedQueuedJobs}.");
            if (request.QueueCleanup && request.QueueTranscode)
                result.Messages.Add("Only the current stored plan type can be queued. Cleanup-first files will need to be re-planned after cleanup replacement before transcode can be queued.");
        }

        return Accepted(result);
    }

    [HttpPost("folder/priority")]
    public async Task<ActionResult<SetMediaFolderPriorityResultDto>> SetFolderPriority(SetMediaFolderPriorityRequest request, CancellationToken cancellationToken)
    {
        if (request.LibraryId <= 0)
            return BadRequest(new { message = "LibraryId is required." });
        if (!request.Cleanup && !request.Transcode)
            return BadRequest(new { message = "Cleanup or Transcode must be enabled." });

        var exists = await db.Libraries.AsNoTracking().AnyAsync(x => x.Id == request.LibraryId, cancellationToken);
        if (!exists) return NotFound(new { message = "Library was not found." });

        var currentPath = NormalizeBrowserPath(request.Path);
        var mediaIds = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == request.LibraryId)
            .Select(x => new { x.Id, x.RelativePath })
            .ToListAsync(cancellationToken);

        var selectedIds = mediaIds
            .Where(x => IsUnderBrowserPath(x.RelativePath, currentPath))
            .Select(x => x.Id)
            .ToHashSet();

        var jobTypes = new List<JobType>();
        if (request.Cleanup) jobTypes.Add(JobType.Cleanup);
        if (request.Transcode) jobTypes.Add(JobType.Transcode);

        var jobs = selectedIds.Count == 0
            ? new List<JobEntity>()
            : await db.Jobs
                .Where(x => x.MediaItemId != null && selectedIds.Contains(x.MediaItemId.Value) && x.Status == JobStatus.Queued && jobTypes.Contains(x.JobType))
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync(cancellationToken);

        foreach (var job in jobs)
            JobPriorityHelper.SetPriority(job, request.Priority);

        await db.SaveChangesAsync(cancellationToken);

        var result = new SetMediaFolderPriorityResultDto
        {
            LibraryId = request.LibraryId,
            Path = currentPath,
            Priority = request.Priority,
            MediaItemsConsidered = selectedIds.Count,
            QueuedJobsUpdated = jobs.Count
        };
        result.Messages.Add($"Priority set to {request.Priority} for {jobs.Count} queued cleanup/transcode job(s) under {(string.IsNullOrWhiteSpace(currentPath) ? "the selected library" : currentPath)}.");
        return Ok(result);
    }


    private static MediaItemDto ToDto(MediaItemEntity item)
    {
        var planSummary = MediaProcessingStatsStore.ReadPlanSummary(item.PlanJson);
        var stats = MediaProcessingStatsStore.Read(item);
        // Stage savings are completed processing facts and must remain visible across replans.
        // Disk-level total/output remain tied to replacement because staged output has not replaced
        // the current file until ReplacedOriginal is true.
        var actualSaved = stats.ReplacedOriginal ? stats.SavedBytes : null;
        var actualCleanupSaved = stats.CleanupSavedBytes;
        var actualTranscodeSaved = stats.TranscodeSavedBytes;
        var actualTotalSaved = stats.ReplacedOriginal ? stats.TotalSavedBytes ?? stats.SavedBytes : null;
        var actualOutputSize = stats.ReplacedOriginal ? stats.OutputSizeBytes : null;

        return new MediaItemDto
        {
            Id = item.Id,
            LibraryId = item.LibraryId,
            RelativePath = item.RelativePath,
            FullPath = item.FullPath,
            Status = item.Status,
            FileSizeBytes = item.FileSizeBytes,
            LastModifiedUtc = item.LastModifiedUtc,
            HasProbe = !string.IsNullOrWhiteSpace(item.ProbeJson),
            HasPlan = !string.IsNullOrWhiteSpace(item.PlanJson),
            PlanHash = item.PlanHash,
            StagingPath = item.StagingPath,
            ActualOutputSizeBytes = actualOutputSize,
            ActualSavedBytes = actualSaved,
            ActualCleanupSavedBytes = actualCleanupSaved,
            ActualTranscodeSavedBytes = actualTranscodeSaved,
            ActualTotalSavedBytes = actualTotalSaved,
            LastCompletedWorkType = stats.LastCompletedWorkType,
            LastWorkCompletedUtc = stats.CompletedUtc,
            StagingTransferComplete = stats.StagingTransferComplete,
            StagingCompleteMarkerPath = stats.StagingCompleteMarkerPath,
            ReplacedOriginal = stats.ReplacedOriginal,
            ReplacementBackupPath = stats.ReplacementBackupPath,
            ReplacedUtc = stats.ReplacedUtc,
            PlanKind = planSummary.PlanKind,
            EstimatedCleanupSavingsBytes = planSummary.EstimatedRemovedBytes,
            EstimatedCleanupOutputBytes = planSummary.EstimatedOutputSizeBytes,
            EstimatedCleanupSavingsComplete = planSummary.EstimatedSavingsComplete,
            OriginalLanguage = item.OriginalLanguage,
            OriginalLanguageSource = item.OriginalLanguageSource,
            MetadataRefreshedUtc = item.MetadataRefreshedUtc,
            MetadataError = item.MetadataError,
            CreatedUtc = item.CreatedUtc,
            UpdatedUtc = item.UpdatedUtc
        };
    }



    private async Task<int> SetQueuedJobPriorityForMediaAsync(long mediaId, IReadOnlyCollection<JobType> jobTypes, JobQueuePriority priority, CancellationToken cancellationToken)
    {
        if (priority == JobQueuePriority.Normal)
            return 0;

        var jobs = await db.Jobs
            .Where(x => x.MediaItemId == mediaId && x.Status == JobStatus.Queued && jobTypes.Contains(x.JobType))
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
            JobPriorityHelper.SetPriority(job, priority);

        if (jobs.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        return jobs.Count;
    }

    private static bool IsUnderBrowserPath(string relativePath, string currentPath)
    {
        var normalized = NormalizeBrowserPath(relativePath);
        if (string.IsNullOrWhiteSpace(currentPath))
            return true;
        return normalized.Equals(currentPath, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(currentPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBrowserPath(string? value) =>
        string.Join('/', (value ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string? ParentPath(string currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath)) return null;
        var slash = currentPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : currentPath[..slash];
    }

    private static List<MediaBrowserCrumbDto> BuildBreadcrumbs(string currentPath)
    {
        var crumbs = new List<MediaBrowserCrumbDto> { new() { Name = "Library", Path = string.Empty } };
        if (string.IsNullOrWhiteSpace(currentPath)) return crumbs;
        var parts = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrWhiteSpace(path) ? part : path + "/" + part;
            crumbs.Add(new MediaBrowserCrumbDto { Name = part, Path = path });
        }
        return crumbs;
    }
}
