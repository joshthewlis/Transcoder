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
[Route("api/review")]
public sealed class ReviewController(TranscoderDbContext db, ReplacementService replacement) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpGet]
    public async Task<PagedResultDto<ReviewItemDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] bool repairLimbo = true,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        if (repairLimbo)
            await MaterializeMissingReviewItemsAsync(cancellationToken);

        var query = db.ReviewItems.AsNoTracking().Where(x => !x.Resolved);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var mediaIds = items.Select(x => x.MediaItemId).Distinct().ToList();
        var mediaById = await db.MediaItems.AsNoTracking()
            .Include(x => x.Library)
            .Where(x => mediaIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return new PagedResultDto<ReviewItemDto>
        {
            Items = items.Select(x => ToDto(x, mediaById.TryGetValue(x.MediaItemId, out var media) ? media : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    [HttpGet("{reviewId:long}")]
    public async Task<ActionResult<ReviewItemDto>> Get(long reviewId, CancellationToken cancellationToken)
    {
        var item = await db.ReviewItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        if (item is null) return NotFound();

        var media = await db.MediaItems.AsNoTracking()
            .Include(x => x.Library)
            .FirstOrDefaultAsync(x => x.Id == item.MediaItemId, cancellationToken);

        return ToDto(item, media);
    }

    [HttpPost("{reviewId:long}/approve")]
    public async Task<IActionResult> Approve(long reviewId, CancellationToken cancellationToken)
    {
        var item = await db.ReviewItems.FirstOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        if (item is null) return NotFound();

        item.Resolved = true;
        item.ResolvedUtc = DateTime.UtcNow;

        var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == item.MediaItemId, cancellationToken);
        if (media is not null)
            ApproveMediaPlanReview(media, "ManualReview", item.Id, item.ResolvedUtc.Value);

        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("approve-all")]
    public async Task<IActionResult> ApproveAll([FromQuery] bool includeLimbo = true, CancellationToken cancellationToken = default)
    {
        if (includeLimbo)
            await MaterializeMissingReviewItemsAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var openReviews = await db.ReviewItems
            .Where(x => !x.Resolved)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var reviewMediaIds = openReviews.Select(x => x.MediaItemId).Distinct().ToList();
        var mediaItems = reviewMediaIds.Count == 0
            ? []
            : await db.MediaItems
                .Where(x => reviewMediaIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

        foreach (var review in openReviews)
        {
            review.Resolved = true;
            review.ResolvedUtc = now;
        }

        var approvedFromReviews = 0;
        foreach (var media in mediaItems)
        {
            if (ApproveMediaPlanReview(media, "BulkApproveAllReviews", null, now))
                approvedFromReviews++;
        }

        var limboCandidates = await db.MediaItems
            .Where(x => x.PlanJson != null && x.PlanJson != "")
            .Where(x => x.Status == MediaStatus.Probed
                || x.Status == MediaStatus.Planning
                || x.Status == MediaStatus.NeedsReview
                || x.Status == MediaStatus.ReadyToCleanup
                || x.Status == MediaStatus.ReadyToTranscode
                || x.Status == MediaStatus.Cleaning
                || x.Status == MediaStatus.Transcoding)
            .ToListAsync(cancellationToken);

        var approvedLimbo = 0;
        foreach (var media in limboCandidates)
        {
            if (HasApprovedPlanReview(media))
                continue;

            if (ApproveMediaPlanReview(media, "BulkApproveAllLimboPlanReviews", null, now))
                approvedLimbo++;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            resolvedReviewItems = openReviews.Count,
            approvedMediaFromReviews = approvedFromReviews,
            approvedLimboMedia = approvedLimbo,
            message = $"Approved {openReviews.Count} visible review item(s) and {approvedLimbo} hidden/limbo plan review(s)."
        });
    }

    [HttpPost("repair-limbo")]
    public async Task<IActionResult> RepairLimbo(CancellationToken cancellationToken = default)
    {
        var created = await MaterializeMissingReviewItemsAsync(cancellationToken);
        return Ok(new
        {
            createdReviewItems = created,
            message = created == 0
                ? "No hidden plan-review limbo items were found."
                : $"Created {created} missing review item(s). Refresh the Review page."
        });
    }

    [HttpPost("{reviewId:long}/skip")]
    public async Task<IActionResult> Skip(long reviewId, CancellationToken cancellationToken)
    {
        var item = await db.ReviewItems.FirstOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        if (item is null) return NotFound();

        var media = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == item.MediaItemId, cancellationToken);
        if (media is not null) media.Status = MediaStatus.Skipped;

        item.Resolved = true;
        item.ResolvedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{reviewId:long}/retry-replace")]
    public async Task<ActionResult<ReplaceMediaResultDto>> RetryReplace(long reviewId, CancellationToken cancellationToken)
    {
        var item = await db.ReviewItems.FirstOrDefaultAsync(x => x.Id == reviewId && !x.Resolved, cancellationToken);
        if (item is null) return NotFound();

        if (!ReplacementService.IsReplaceFailureReason(item.Reason))
        {
            return BadRequest(new ReplaceMediaResultDto
            {
                MediaId = item.MediaItemId,
                Accepted = false,
                Replaced = false,
                Message = "This review item is not a failed replacement review."
            });
        }

        var result = await replacement.ReplaceMediaAsync(item.MediaItemId, cancellationToken);
        if (result.Replaced)
        {
            var now = DateTime.UtcNow;
            var reviews = await db.ReviewItems
                .Where(x => x.MediaItemId == item.MediaItemId && !x.Resolved && x.Reason == "Replace original failed.")
                .ToListAsync(cancellationToken);
            foreach (var review in reviews)
            {
                review.Resolved = true;
                review.ResolvedUtc = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result.Replaced ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{reviewId:long}/stream-actions")]
    public IActionResult StreamActions(long reviewId) => Accepted(new
    {
        reviewId,
        message = "Manual stream action support is reserved for the planner implementation pass."
    });

    private async Task<int> MaterializeMissingReviewItemsAsync(CancellationToken cancellationToken)
    {
        var activePlanReviewIds = await db.Jobs.AsNoTracking()
            .Where(x => x.MediaItemId != null
                && x.JobType == JobType.PlanReview
                && (x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .Select(x => x.MediaItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var activePlanReviewSet = activePlanReviewIds.ToHashSet();

        var openReviewIds = await db.ReviewItems.AsNoTracking()
            .Where(x => !x.Resolved)
            .Select(x => x.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var openReviewSet = openReviewIds.ToHashSet();

        var candidates = await db.MediaItems.AsNoTracking()
            .Include(x => x.Library)
            .Where(x => x.PlanJson != null && x.PlanJson != "")
            .Where(x => x.Status == MediaStatus.Probed
                || x.Status == MediaStatus.Planning
                || x.Status == MediaStatus.NeedsReview
                || x.Status == MediaStatus.ReadyToCleanup
                || x.Status == MediaStatus.ReadyToTranscode
                || x.Status == MediaStatus.Cleaning
                || x.Status == MediaStatus.Transcoding)
            .OrderBy(x => x.RelativePath)
            .Take(10000)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var media in candidates)
        {
            if (openReviewSet.Contains(media.Id) || activePlanReviewSet.Contains(media.Id) || HasApprovedPlanReview(media))
                continue;

            var plan = TryReadPlan(media.PlanJson!);
            if (plan is null)
                continue;

            var reason = plan.BlockingReasons.Count > 0
                ? string.Join("; ", plan.BlockingReasons.Take(3))
                : "Plan review is required and has not approved this plan yet.";

            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = plan.BlockingReasons.Count > 0 ? ReviewType.PolicyAmbiguous : ReviewType.PlanReviewFailed,
                Severity = ReviewSeverity.Blocking,
                Reason = reason,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    source = "ReviewController.RepairLimbo",
                    mediaId = media.Id,
                    media.RelativePath,
                    media.Status,
                    plan,
                    reasons = plan.BlockingReasons
                }, JsonOptions)
            });
            created++;
        }

        if (created > 0)
            await db.SaveChangesAsync(cancellationToken);

        return created;
    }

    private static bool ApproveMediaPlanReview(MediaItemEntity media, string source, long? reviewId, DateTime approvedUtc)
    {
        if (string.IsNullOrWhiteSpace(media.PlanJson))
            return false;

        var plan = TryReadPlan(media.PlanJson);
        media.PlanReviewJson = JsonSerializer.Serialize(new
        {
            reviewStatus = "Approved",
            source,
            reviewId,
            approvedUtc,
            planHash = media.PlanHash,
            bulkApproved = source.Contains("Bulk", StringComparison.OrdinalIgnoreCase)
        }, JsonOptions);
        media.PlanReviewedUtc = approvedUtc;
        media.UpdatedUtc = DateTime.UtcNow;

        media.Status = plan is null
            ? MediaStatus.ReadyToTranscode
            : IsNoActionPlan(plan)
                ? MediaStatus.Skipped
                : IsCleanupPlan(plan)
                    ? MediaStatus.ReadyToCleanup
                    : MediaStatus.ReadyToTranscode;

        return true;
    }

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

    private static TranscodePlanDto? TryReadPlan(string planJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscodePlanDto>(planJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool IsCleanupPlan(TranscodePlanDto plan) =>
        plan.PlanKind.Equals("CleanupOnly", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoActionPlan(TranscodePlanDto plan) =>
        plan.PlanKind.Equals("NoAction", StringComparison.OrdinalIgnoreCase);

    private static ReviewItemDto ToDto(ReviewItemEntity item, MediaItemEntity? media) => new()
    {
        Id = item.Id,
        MediaItemId = item.MediaItemId,
        LibraryId = media?.LibraryId,
        LibraryName = media?.Library?.Name,
        MediaName = media is null ? null : Path.GetFileName(media.RelativePath),
        MediaRelativePath = media?.RelativePath,
        MediaFullPath = media?.FullPath,
        ReviewType = item.ReviewType,
        Severity = item.Severity,
        Reason = item.Reason,
        DetailsJson = item.DetailsJson,
        Resolved = item.Resolved,
        CreatedUtc = item.CreatedUtc
    };
}
