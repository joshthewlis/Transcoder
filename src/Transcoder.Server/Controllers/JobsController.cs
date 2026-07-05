using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController(TranscoderDbContext db, JobLeaseService leaseService, JobCompletionService completionService) : ControllerBase
{
    [HttpPost("lease-batch")]
    public async Task<ActionResult<JobLeaseBatchResponse>> LeaseBatch(LeaseBatchRequest request, CancellationToken cancellationToken) =>
        await leaseService.LeaseBatchAsync(request, cancellationToken);

    [HttpPost("{jobId:long}/progress")]
    public async Task<IActionResult> Progress(long jobId, JobProgressRequest request, CancellationToken cancellationToken) =>
        await completionService.ProgressAsync(jobId, request, cancellationToken) ? NoContent() : Conflict(new { message = "Job not found or lease is stale." });

    [HttpPost("{jobId:long}/complete")]
    public async Task<IActionResult> Complete(long jobId, JobCompleteRequest request, CancellationToken cancellationToken) =>
        await completionService.CompleteAsync(jobId, request, cancellationToken) ? NoContent() : Conflict(new { message = "Job not found or lease is stale." });

    [HttpPost("{jobId:long}/fail")]
    public async Task<IActionResult> Fail(long jobId, JobFailRequest request, CancellationToken cancellationToken) =>
        await completionService.FailAsync(jobId, request, cancellationToken) ? NoContent() : Conflict(new { message = "Job not found or lease is stale." });

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<JobDto>>> List(
        [FromQuery] JobStatus? status,
        [FromQuery] JobType? jobType,
        [FromQuery] string? workerId,
        [FromQuery] bool activeOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var query = db.Jobs.AsNoTracking();
        if (activeOnly)
            query = query.Where(x => x.Status == JobStatus.Queued || x.Status == JobStatus.Leased || x.Status == JobStatus.Running);
        if (status is not null) query = query.Where(x => x.Status == status.Value);
        if (jobType is not null) query = query.Where(x => x.JobType == jobType.Value);
        if (!string.IsNullOrWhiteSpace(workerId))
        {
            var filter = workerId.Trim();
            query = query.Where(x => x.LeasedByWorkerId != null && x.LeasedByWorkerId.Contains(filter));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var dtoItems = await ToDtosAsync(items, cancellationToken);
        return new PagedResultDto<JobDto> { Items = dtoItems, Page = page, PageSize = pageSize, TotalCount = total };
    }

    [HttpGet("{jobId:long}")]
    public async Task<ActionResult<JobDto>> Get(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null) return NotFound();

        var dto = (await ToDtosAsync([job], cancellationToken)).Single();
        return dto;
    }

    [HttpDelete("finished")]
    public async Task<IActionResult> ClearFinished([FromQuery] bool includeFailed = false, CancellationToken cancellationToken = default)
    {
        var statuses = includeFailed
            ? new[] { JobStatus.Completed, JobStatus.Cancelled, JobStatus.Expired, JobStatus.Failed }
            : new[] { JobStatus.Completed, JobStatus.Cancelled, JobStatus.Expired };

        var jobs = await db.Jobs.Where(x => statuses.Contains(x.Status)).ToListAsync(cancellationToken);
        db.Jobs.RemoveRange(jobs);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { deleted = jobs.Count });
    }

    [HttpPost("{jobId:long}/retry")]
    public async Task<IActionResult> Retry(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null) return NotFound();
        job.Status = JobStatus.Queued;
        job.LeaseId = null;
        job.LeasedByWorkerId = null;
        job.LeasedByWorkerInstanceId = null;
        job.LeaseStartedUtc = null;
        job.LeaseLastSeenUtc = null;
        job.LeaseExpiresUtc = null;
        job.Progress = null;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{jobId:long}/cancel")]
    public async Task<IActionResult> Cancel(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null) return NotFound();
        job.Status = JobStatus.Cancelled;
        job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<List<JobDto>> ToDtosAsync(IReadOnlyCollection<JobEntity> jobs, CancellationToken cancellationToken)
    {
        var libraryIds = jobs.Select(x => x.LibraryId).OfType<int>().Distinct().ToList();
        var mediaIds = jobs.Select(x => x.MediaItemId).OfType<long>().Distinct().ToList();

        var libraries = libraryIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Libraries.AsNoTracking()
                .Where(x => libraryIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var mediaItems = mediaIds.Count == 0
            ? new Dictionary<long, MediaItemEntity>()
            : await db.MediaItems.AsNoTracking()
                .Where(x => mediaIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);

        return jobs.Select(job => ToDto(job, libraries, mediaItems)).ToList();
    }

    private static JobDto ToDto(JobEntity job, IReadOnlyDictionary<int, string> libraries, IReadOnlyDictionary<long, MediaItemEntity> mediaItems)
    {
        MediaItemEntity? media = null;
        if (job.MediaItemId is not null)
            mediaItems.TryGetValue(job.MediaItemId.Value, out media);

        string? libraryName = null;
        if (job.LibraryId is not null)
            libraries.TryGetValue(job.LibraryId.Value, out libraryName);

        return new JobDto
        {
            Id = job.Id,
            JobType = job.JobType,
            Status = job.Status,
            LibraryId = job.LibraryId,
            LibraryName = libraryName,
            MediaItemId = job.MediaItemId,
            MediaName = media is null ? null : GetDisplayName(media.RelativePath),
            MediaRelativePath = media?.RelativePath,
            MediaFullPath = media?.FullPath,
            RequiredEncoder = job.RequiredEncoder,
            RequiredEncoderEngine = ReadRequiredEncoderEngine(job),
            LeaseId = job.LeaseId,
            LeasedByWorkerId = job.LeasedByWorkerId,
            AttemptNumber = job.AttemptNumber,
            MaxAttempts = job.MaxAttempts,
            Progress = job.Progress,
            LastMessage = job.LastMessage,
            LastError = job.LastError,
            CreatedUtc = job.CreatedUtc,
            CompletedUtc = job.CompletedUtc
        };
    }


    private static EncoderEngine ReadRequiredEncoderEngine(JobEntity job)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            var fromPayload = ReadEncoderEngine(root, "requiredEncoderEngine");
            if (fromPayload != EncoderEngine.Unknown)
                return fromPayload;

            if (root.TryGetProperty("planJson", out var planElement))
            {
                var fromPlan = ReadEncoderEngine(planElement, "requiredEncoderEngine");
                if (fromPlan != EncoderEngine.Unknown)
                    return fromPlan;
            }
        }
        catch
        {
            // Legacy payload; infer below.
        }

        return InferEncoderEngine(job.RequiredEncoder);
    }

    private static EncoderEngine ReadEncoderEngine(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var engineElement))
            return EncoderEngine.Unknown;

        var value = engineElement.ValueKind == System.Text.Json.JsonValueKind.String ? engineElement.GetString() : engineElement.ToString();
        return Enum.TryParse<EncoderEngine>(value, ignoreCase: true, out var engine) ? engine : EncoderEngine.Unknown;
    }

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

    private static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[(index + 1)..] : normalized;
    }
}
