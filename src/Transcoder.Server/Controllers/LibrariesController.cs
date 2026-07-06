using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/libraries")]
public sealed class LibrariesController(TranscoderDbContext db, ScanQueue scanQueue, LibraryWatcherState watcherState, MetadataRefreshService metadataRefresh, TranscodePlanService planner, ReplacementService replacement) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [HttpGet]
    public async Task<ActionResult<List<LibraryDto>>> List(CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return libraries.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<LibraryDto>> Create(CreateLibraryRequest request, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(request.RootPath);
        if (!Directory.Exists(fullPath))
            return BadRequest(new { message = "Library root path does not exist on the server.", path = fullPath });

        var entity = new LibraryEntity
        {
            Name = request.Name,
            RootPath = fullPath,
            Enabled = request.Enabled,
            PolicyJson = JsonSerializer.Serialize(request.Policy, JsonOptions),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.Libraries.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await MarkWorkersForPathCheckAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { libraryId = entity.Id }, ToDto(entity));
    }

    [HttpGet("{libraryId:int}")]
    public async Task<ActionResult<LibraryDto>> Get(int libraryId, CancellationToken cancellationToken)
    {
        var entity = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        return entity is null ? NotFound() : ToDto(entity);
    }

    [HttpPut("{libraryId:int}")]
    public async Task<IActionResult> Update(int libraryId, UpdateLibraryRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.Libraries.FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (entity is null) return NotFound();

        entity.Name = request.Name;
        entity.RootPath = Path.GetFullPath(request.RootPath);
        entity.Enabled = request.Enabled;
        entity.PolicyJson = JsonSerializer.Serialize(request.Policy, JsonOptions);
        entity.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await MarkWorkersForPathCheckAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{libraryId:int}/policy")]
    public async Task<ActionResult<LibraryPolicyDto>> GetPolicy(int libraryId, CancellationToken cancellationToken)
    {
        var entity = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        return entity is null ? NotFound() : DeserializePolicy(entity.PolicyJson);
    }

    [HttpPut("{libraryId:int}/policy")]
    public async Task<IActionResult> SetPolicy(int libraryId, LibraryPolicyDto policy, CancellationToken cancellationToken)
    {
        var entity = await db.Libraries.FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (entity is null) return NotFound();
        entity.PolicyJson = JsonSerializer.Serialize(policy, JsonOptions);
        entity.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("watch-status")]
    public ActionResult<List<LibraryWatchStatusDto>> WatchStatus()
    {
        return watcherState.GetAll().ToList();
    }

    [HttpGet("{libraryId:int}/watch-status")]
    public ActionResult<LibraryWatchStatusDto> WatchStatus(int libraryId)
    {
        var status = watcherState.Get(libraryId);
        return status is null ? NotFound() : status;
    }

    [HttpPost("{libraryId:int}/metadata/refresh")]
    public async Task<ActionResult<MetadataRefreshResultDto>> RefreshMetadata(int libraryId, [FromQuery] bool force = true, CancellationToken cancellationToken = default)
    {
        var exists = await db.Libraries.AnyAsync(x => x.Id == libraryId, cancellationToken);
        if (!exists) return NotFound();
        return await metadataRefresh.RefreshLibraryAsync(libraryId, force, cancellationToken);
    }

    [HttpPost("{libraryId:int}/scan")]
    public async Task<IActionResult> Scan(int libraryId, ScanLibraryRequest request, CancellationToken cancellationToken)
    {
        var exists = await db.Libraries.AnyAsync(x => x.Id == libraryId, cancellationToken);
        if (!exists) return NotFound();
        await MarkWorkersForPathCheckAsync(cancellationToken);
        await scanQueue.QueueAsync(libraryId, cancellationToken);
        return Accepted(new { libraryId, queued = true });
    }



    [HttpPost("{libraryId:int}/pilot-run")]
    public async Task<ActionResult<PilotRunResultDto>> PilotRun(int libraryId, PilotRunRequestDto request, CancellationToken cancellationToken)
    {
        var result = await planner.QueueLibraryPilotRunAsync(libraryId, request, cancellationToken);
        return result.Messages.Any(x => x == "Library was not found.") ? NotFound(result) : Accepted(result);
    }

    [HttpPost("{libraryId:int}/cleanup/queue")]
    public async Task<ActionResult<QueueLibraryWorkResultDto>> QueueCleanup(int libraryId, CancellationToken cancellationToken)
    {
        var result = await planner.QueueLibraryCleanupFromExistingPlansAsync(libraryId, cancellationToken);
        return result.Messages.Any(x => x == "Library was not found.") ? NotFound(result) : Accepted(result);
    }

    [HttpPost("{libraryId:int}/transcode/queue")]
    public async Task<ActionResult<QueueLibraryWorkResultDto>> QueueTranscode(int libraryId, CancellationToken cancellationToken)
    {
        var result = await planner.QueueLibraryTranscodeFromExistingPlansAsync(libraryId, cancellationToken);
        return result.Messages.Any(x => x == "Library was not found.") ? NotFound(result) : Accepted(result);
    }

    [HttpPost("{libraryId:int}/replace")]
    public async Task<ActionResult<ReplaceLibraryResultDto>> ReplaceLibrary(int libraryId, CancellationToken cancellationToken)
    {
        var exists = await db.Libraries.AnyAsync(x => x.Id == libraryId, cancellationToken);
        if (!exists) return NotFound();
        return Ok(await replacement.ReplaceLibraryAsync(libraryId, cancellationToken));
    }

    [HttpGet("{libraryId:int}/scan-status")]
    public async Task<ActionResult<LibraryScanStatusDto>> ScanStatus(int libraryId, CancellationToken cancellationToken)
    {
        var entity = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (entity is null) return NotFound();

        return new LibraryScanStatusDto
        {
            LibraryId = entity.Id,
            IsScanning = entity.IsScanning,
            FoldersScanned = entity.FoldersScanned,
            FilesDiscovered = entity.FilesDiscovered,
            MediaItemsCreated = entity.MediaItemsCreated,
            MediaItemsUpdated = entity.MediaItemsUpdated,
            ProbeJobsCreated = entity.ProbeJobsCreated,
            StartedUtc = entity.ScanStartedUtc,
            CompletedUtc = entity.ScanCompletedUtc,
            LastError = entity.ScanLastError
        };
    }

    private async Task MarkWorkersForPathCheckAsync(CancellationToken cancellationToken)
    {
        var workers = await db.Workers
            .Where(x => x.ControlState != WorkerControlState.Disabled && x.State != WorkerState.RequirementsFailed)
            .ToListAsync(cancellationToken);

        foreach (var worker in workers)
        {
            if (worker.State is WorkerState.Lost or WorkerState.Unresponsive)
                continue;

            worker.State = WorkerState.PathCheckRequired;
        }

        if (workers.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static LibraryDto ToDto(LibraryEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        RootPath = entity.RootPath,
        Enabled = entity.Enabled,
        Policy = DeserializePolicy(entity.PolicyJson),
        CreatedUtc = entity.CreatedUtc,
        UpdatedUtc = entity.UpdatedUtc
    };

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto(); }
        catch { return new LibraryPolicyDto(); }
    }
}
