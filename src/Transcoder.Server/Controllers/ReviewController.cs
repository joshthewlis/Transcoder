using System.Text.Json;
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
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<ReviewItemDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var query = db.ReviewItems.AsNoTracking().Where(x => !x.Resolved);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var mediaIds = items.Select(x => x.MediaItemId).Distinct().ToList();
        var mediaById = await db.MediaItems.AsNoTracking()
            .Include(x => x.Library)
            .Where(x => mediaIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        return new PagedResultDto<ReviewItemDto> { Items = items.Select(x => ToDto(x, mediaById.TryGetValue(x.MediaItemId, out var media) ? media : null)).ToList(), Page = page, PageSize = pageSize, TotalCount = total };
    }

    [HttpGet("{reviewId:long}")]
    public async Task<ActionResult<ReviewItemDto>> Get(long reviewId, CancellationToken cancellationToken)
    {
        var item = await db.ReviewItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        if (item is null) return NotFound();
        var media = await db.MediaItems.AsNoTracking().Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == item.MediaItemId, cancellationToken);
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
        if (media is not null && !string.IsNullOrWhiteSpace(media.PlanJson))
        {
            var plan = TryReadPlan(media.PlanJson);
            media.PlanReviewJson = JsonSerializer.Serialize(new
            {
                reviewStatus = "Approved",
                source = "ManualReview",
                reviewId = item.Id,
                reviewType = item.ReviewType.ToString(),
                approvedUtc = item.ResolvedUtc,
                planHash = media.PlanHash
            });
            media.PlanReviewedUtc = DateTime.UtcNow;
            media.UpdatedUtc = DateTime.UtcNow;

            media.Status = plan is null
                ? MediaStatus.ReadyToTranscode
                : IsNoActionPlan(plan)
                    ? MediaStatus.Skipped
                    : IsCleanupPlan(plan)
                        ? MediaStatus.ReadyToCleanup
                        : MediaStatus.ReadyToTranscode;
        }

        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
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
            return BadRequest(new ReplaceMediaResultDto
            {
                MediaId = item.MediaItemId,
                Accepted = false,
                Replaced = false,
                Message = "This review item is not a failed replacement review."
            });

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
    public IActionResult StreamActions(long reviewId) => Accepted(new { reviewId, message = "Manual stream action support is reserved for the planner implementation pass." });

    private static TranscodePlanDto? TryReadPlan(string planJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscodePlanDto>(planJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCleanupPlan(TranscodePlanDto plan) => plan.PlanKind.Equals("CleanupOnly", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoActionPlan(TranscodePlanDto plan) => plan.PlanKind.Equals("NoAction", StringComparison.OrdinalIgnoreCase);

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
