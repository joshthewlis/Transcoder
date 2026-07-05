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

    private static MediaItemDto ToDto(MediaItemEntity item)
    {
        var planSummary = MediaProcessingStatsStore.ReadPlanSummary(item.PlanJson);
        var stats = MediaProcessingStatsStore.Read(item);
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
            ActualOutputSizeBytes = stats.OutputSizeBytes,
            ActualSavedBytes = stats.SavedBytes,
            ActualCleanupSavedBytes = stats.CleanupSavedBytes,
            ActualTranscodeSavedBytes = stats.TranscodeSavedBytes,
            ActualTotalSavedBytes = stats.TotalSavedBytes ?? stats.SavedBytes,
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
