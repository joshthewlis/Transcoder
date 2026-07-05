using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/workers")]
public sealed class WorkersController(
    TranscoderDbContext db,
    PathCheckDefinitionService pathChecks,
    SystemSettingsService settings,
    IOptions<WorkerTimingOptions> timingOptions,
    IOptions<TranscoderServerOptions> serverOptions,
    IOptions<WorkerRequirementOptions> requirementOptions) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [HttpPost("register")]
    public async Task<ActionResult<WorkerRegisterResponse>> Register(WorkerRegisterRequest request, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == request.WorkerId, cancellationToken);
        var now = DateTime.UtcNow;
        var isNew = worker is null;

        worker ??= new WorkerEntity { WorkerId = request.WorkerId, RegisteredUtc = now };
        worker.WorkerName = request.WorkerName;
        worker.WorkerInstanceId = request.WorkerInstanceId;
        worker.WorkerVersion = request.WorkerVersion;
        worker.Roles = request.Roles;
        worker.LastSeenUtc = now;
        worker.MappingConfigHash = request.PathMapping.MappingConfigHash;
        worker.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities, JsonOptions);
        worker.LimitsJson = JsonSerializer.Serialize(request.Limits, JsonOptions);
        worker.LocalStorageJson = JsonSerializer.Serialize(request.LocalStorage, JsonOptions);
        worker.ShutdownJson = JsonSerializer.Serialize(request.Shutdown, JsonOptions);
        worker.ServerPrefixesJson = JsonSerializer.Serialize(request.PathMapping.ServerPrefixes, JsonOptions);

        var requirementErrors = ValidateWorkerRequirements(request);
        if (requirementErrors.Count > 0 && requirementOptions.Value.RejectWorkersWithMissingRequiredTools)
        {
            worker.State = WorkerState.RequirementsFailed;
            worker.ControlState = WorkerControlState.Disabled;
            if (isNew) db.Workers.Add(worker);
            await db.SaveChangesAsync(cancellationToken);

            var timingForRejected = timingOptions.Value;
            return new WorkerRegisterResponse
            {
                Accepted = false,
                ServerTimeUtc = now.ToString("O"),
                ApiVersion = serverOptions.Value.ApiVersion,
                ExpectedHeartbeatSeconds = timingForRejected.ExpectedHeartbeatSeconds,
                UnresponsiveAfterSeconds = timingForRejected.UnresponsiveAfterSeconds,
                LostAfterSeconds = timingForRejected.LostAfterSeconds,
                PollSeconds = serverOptions.Value.PollSeconds,
                MaxLeaseSeconds = timingForRejected.MaxLeaseSeconds,
                PathCheckRequired = false,
                ProcessingMode = await settings.GetProcessingModeAsync(cancellationToken),
                ControlState = WorkerControlState.Disabled,
                Message = "Worker registration rejected because local tool requirements were not met.",
                RequirementErrors = requirementErrors
            };
        }

        worker.State = WorkerState.PathCheckRequired;
        if (isNew) db.Workers.Add(worker);
        await db.SaveChangesAsync(cancellationToken);

        var timing = timingOptions.Value;
        return new WorkerRegisterResponse
        {
            Accepted = true,
            ServerTimeUtc = now.ToString("O"),
            ApiVersion = serverOptions.Value.ApiVersion,
            ExpectedHeartbeatSeconds = timing.ExpectedHeartbeatSeconds,
            UnresponsiveAfterSeconds = timing.UnresponsiveAfterSeconds,
            LostAfterSeconds = timing.LostAfterSeconds,
            PollSeconds = serverOptions.Value.PollSeconds,
            MaxLeaseSeconds = timing.MaxLeaseSeconds,
            PathCheckRequired = true,
            PathChecks = await pathChecks.BuildPathChecksAsync(cancellationToken),
            ProcessingMode = await settings.GetProcessingModeAsync(cancellationToken),
            ControlState = worker.ControlState,
            Message = requirementErrors.Count == 0 ? null : "Worker accepted with requirement warnings.",
            RequirementErrors = requirementErrors
        };
    }

    [HttpPost("{workerId}/path-checks")]
    public async Task<IActionResult> PathChecks(string workerId, WorkerPathCheckResultsRequest request, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();

        worker.MappingConfigHash = request.MappingConfigHash;

        foreach (var result in request.Results)
        {
            db.WorkerPathChecks.Add(new WorkerPathCheckEntity
            {
                WorkerId = workerId,
                WorkerInstanceId = request.WorkerInstanceId,
                MappingConfigHash = request.MappingConfigHash,
                CheckId = result.Id,
                LibraryId = result.LibraryId,
                ServerPath = result.ServerPath,
                Success = result.Success,
                CanRead = result.CanRead,
                CanWrite = result.CanWrite,
                Created = result.Created,
                Error = result.Error,
                CheckedUtc = DateTime.UtcNow
            });
        }

        worker.State = request.Results.All(x => x.Success)
            ? WorkerState.Online
            : request.Results.Any(x => x.Success)
                ? WorkerState.PartialPathAccess
                : WorkerState.PathCheckFailed;
        worker.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{workerId}/heartbeat")]
    public async Task<ActionResult<WorkerHeartbeatResponse>> Heartbeat(string workerId, WorkerHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();

        worker.WorkerInstanceId = request.WorkerInstanceId;
        var now = DateTime.UtcNow;
        worker.LastSeenUtc = now;
        if (worker.State != WorkerState.PathCheckFailed && worker.State != WorkerState.RequirementsFailed && worker.ControlState == WorkerControlState.Normal)
            worker.State = worker.State == WorkerState.PartialPathAccess ? WorkerState.PartialPathAccess : WorkerState.Online;

        foreach (var activeJob in request.ActiveJobs)
        {
            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == activeJob.JobId && x.LeaseId == activeJob.LeaseId, cancellationToken);
            if (job is null) continue;
            job.Status = JobStatus.Running;
            job.Progress = activeJob.Progress;
            job.LastMessage = activeJob.Message;
            job.LeaseLastSeenUtc = now;
            job.LeaseExpiresUtc = now.AddSeconds(timingOptions.Value.MaxLeaseSeconds);
        }

        await db.SaveChangesAsync(cancellationToken);

        var response = new WorkerHeartbeatResponse
        {
            Accepted = true,
            ControlState = worker.ControlState,
            AcceptNewWork = worker.ControlState == WorkerControlState.Normal && worker.State != WorkerState.RequirementsFailed && worker.State != WorkerState.PathCheckFailed,
            ShutdownWhenIdle = worker.ControlState is WorkerControlState.DrainThenExit or WorkerControlState.DrainThenShutdown,
            ShutdownAction = worker.ControlState switch
            {
                WorkerControlState.DrainThenExit => "ExitProcess",
                WorkerControlState.DrainThenShutdown => "ShutdownHost",
                _ => "Disabled"
            },
            PathCheckRequired = worker.State == WorkerState.PathCheckRequired,
            PathChecks = worker.State == WorkerState.PathCheckRequired ? await pathChecks.BuildPathChecksAsync(cancellationToken) : []
        };

        return response;
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkerDto>>> List(CancellationToken cancellationToken)
    {
        var workers = await db.Workers.AsNoTracking().OrderBy(x => x.WorkerId).ToListAsync(cancellationToken);
        var workerIds = workers.Select(x => x.WorkerId).ToList();
        var activeJobs = await db.Jobs.AsNoTracking()
            .Where(x => x.LeasedByWorkerId != null
                && workerIds.Contains(x.LeasedByWorkerId)
                && (x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .OrderBy(x => x.LeaseStartedUtc)
            .ToListAsync(cancellationToken);

        var activeByWorker = activeJobs
            .GroupBy(x => x.LeasedByWorkerId!)
            .ToDictionary(x => x.Key, x => x.ToList());

        return workers.Select(worker => ToDto(worker, activeByWorker.TryGetValue(worker.WorkerId, out var jobs) ? jobs : [])).ToList();
    }

    [HttpGet("{workerId}")]
    public async Task<ActionResult<WorkerDto>> Get(string workerId, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();

        var activeJobs = await db.Jobs.AsNoTracking()
            .Where(x => x.LeasedByWorkerId == workerId && (x.Status == JobStatus.Leased || x.Status == JobStatus.Running))
            .OrderBy(x => x.LeaseStartedUtc)
            .ToListAsync(cancellationToken);

        return ToDto(worker, activeJobs);
    }


    [HttpGet("{workerId}/path-checks")]
    public async Task<ActionResult<List<WorkerPathCheckDto>>> GetPathChecks(string workerId, CancellationToken cancellationToken)
    {
        var exists = await db.Workers.AsNoTracking().AnyAsync(x => x.WorkerId == workerId, cancellationToken);
        if (!exists) return NotFound();

        var checks = await db.WorkerPathChecks.AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderByDescending(x => x.CheckedUtc)
            .ThenBy(x => x.CheckId)
            .Take(200)
            .Select(x => new WorkerPathCheckDto
            {
                Id = x.Id,
                WorkerId = x.WorkerId,
                WorkerInstanceId = x.WorkerInstanceId,
                MappingConfigHash = x.MappingConfigHash,
                CheckId = x.CheckId,
                LibraryId = x.LibraryId,
                ServerPath = x.ServerPath,
                Success = x.Success,
                CanRead = x.CanRead,
                CanWrite = x.CanWrite,
                Created = x.Created,
                Error = x.Error,
                CheckedUtc = x.CheckedUtc
            })
            .ToListAsync(cancellationToken);

        return checks;
    }

    [HttpGet("{workerId}/path-checks/latest")]
    public async Task<ActionResult<List<WorkerPathCheckDto>>> GetLatestPathChecks(string workerId, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();

        var rows = await db.WorkerPathChecks.AsNoTracking()
            .Where(x => x.WorkerId == workerId && x.MappingConfigHash == worker.MappingConfigHash)
            .OrderByDescending(x => x.CheckedUtc)
            .ToListAsync(cancellationToken);

        var checks = rows
            .GroupBy(x => x.CheckId)
            .Select(g => g.First())
            .OrderBy(x => x.CheckId)
            .Select(x => new WorkerPathCheckDto
            {
                Id = x.Id,
                WorkerId = x.WorkerId,
                WorkerInstanceId = x.WorkerInstanceId,
                MappingConfigHash = x.MappingConfigHash,
                CheckId = x.CheckId,
                LibraryId = x.LibraryId,
                ServerPath = x.ServerPath,
                Success = x.Success,
                CanRead = x.CanRead,
                CanWrite = x.CanWrite,
                Created = x.Created,
                Error = x.Error,
                CheckedUtc = x.CheckedUtc
            })
            .ToList();

        return checks;
    }

    [HttpGet("{workerId}/runtime-settings")]
    public async Task<ActionResult<WorkerRuntimeSettingsDto>> GetRuntimeSettings(string workerId, CancellationToken cancellationToken)
    {
        var exists = await db.Workers.AsNoTracking().AnyAsync(x => x.WorkerId == workerId, cancellationToken);
        if (!exists) return NotFound();
        return await settings.GetWorkerRuntimeSettingsAsync(workerId, cancellationToken);
    }

    [HttpPut("{workerId}/runtime-settings")]
    public async Task<ActionResult<WorkerRuntimeSettingsDto>> SetRuntimeSettings(string workerId, UpdateWorkerRuntimeSettingsRequest request, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();

        var runtime = new WorkerRuntimeSettingsDto
        {
            PathMappings = request.PathMappings
                .Where(x => !string.IsNullOrWhiteSpace(x.ServerPrefix) && !string.IsNullOrWhiteSpace(x.WorkerPrefix))
                .Select(x => new WorkerPathMappingDto
                {
                    ServerPrefix = x.ServerPrefix.TrimEnd('/', '\\'),
                    WorkerPrefix = x.WorkerPrefix.TrimEnd('/', '\\')
                })
                .ToList()
        };

        await settings.SetWorkerRuntimeSettingsAsync(workerId, runtime, cancellationToken);
        worker.ServerPrefixesJson = JsonSerializer.Serialize(runtime.PathMappings.Select(x => x.ServerPrefix).ToList(), JsonOptions);
        worker.State = WorkerState.PathCheckRequired;
        await db.SaveChangesAsync(cancellationToken);
        return runtime;
    }

    [HttpPost("{workerId}/path-checks/request")]
    public async Task<IActionResult> RequestPathCheck(string workerId, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();
        worker.State = WorkerState.PathCheckRequired;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{workerId}/control")]
    public async Task<IActionResult> Control(string workerId, SetWorkerControlStateRequest request, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();
        worker.ControlState = request.ControlState;
        worker.LastControlReason = request.Reason;
        worker.State = request.ControlState switch
        {
            WorkerControlState.Disabled => WorkerState.Disabled,
            WorkerControlState.Normal when worker.State is WorkerState.Disabled or WorkerState.Draining => WorkerState.PathCheckRequired,
            WorkerControlState.Normal => worker.State,
            _ => WorkerState.Draining
        };
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{workerId}/drain")]
    public Task<IActionResult> Drain(string workerId, CancellationToken cancellationToken) =>
        SetControl(workerId, WorkerControlState.Drain, cancellationToken);

    [HttpPost("{workerId}/enable")]
    public Task<IActionResult> Enable(string workerId, CancellationToken cancellationToken) =>
        SetControl(workerId, WorkerControlState.Normal, cancellationToken);

    [HttpPost("{workerId}/disable")]
    public Task<IActionResult> Disable(string workerId, CancellationToken cancellationToken) =>
        SetControl(workerId, WorkerControlState.Disabled, cancellationToken);

    private async Task<IActionResult> SetControl(string workerId, WorkerControlState state, CancellationToken cancellationToken)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == workerId, cancellationToken);
        if (worker is null) return NotFound();
        worker.ControlState = state;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private List<string> ValidateWorkerRequirements(WorkerRegisterRequest request)
    {
        var options = requirementOptions.Value;
        var errors = new List<string>();

        if (options.RequireFfmpegForTranscoderRole && request.Roles.HasFlag(WorkerRole.Transcoder) && !request.Capabilities.Ffmpeg.Available)
            errors.Add("Worker has Transcoder role but ffmpeg is not available.");

        if (options.RequireFfprobeForProberRole && request.Roles.HasFlag(WorkerRole.Prober) && !request.Capabilities.Ffprobe.Available)
            errors.Add("Worker has Prober role but ffprobe is not available.");

        if (options.RequireFfprobeForValidatorRole && request.Roles.HasFlag(WorkerRole.Validator) && !request.Capabilities.Ffprobe.Available)
            errors.Add("Worker has Validator role but ffprobe is not available.");

        if (!string.IsNullOrWhiteSpace(options.MinimumFfmpegVersion) && request.Capabilities.Ffmpeg.Available)
        {
            var actual = request.Capabilities.Ffmpeg.Version ?? ExtractVersion(request.Capabilities.Ffmpeg.VersionText);
            if (!VersionMeetsMinimum(actual, options.MinimumFfmpegVersion))
                errors.Add($"ffmpeg version {actual ?? "unknown"} is below required minimum {options.MinimumFfmpegVersion}.");
        }

        if (!string.IsNullOrWhiteSpace(options.MinimumFfprobeVersion) && request.Capabilities.Ffprobe.Available)
        {
            var actual = request.Capabilities.Ffprobe.Version ?? ExtractVersion(request.Capabilities.Ffprobe.VersionText);
            if (!VersionMeetsMinimum(actual, options.MinimumFfprobeVersion))
                errors.Add($"ffprobe version {actual ?? "unknown"} is below required minimum {options.MinimumFfprobeVersion}.");
        }

        return errors;
    }

    private static bool VersionMeetsMinimum(string? actual, string minimum)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        if (!Version.TryParse(NormalizeVersion(actual), out var actualVersion)) return false;
        if (!Version.TryParse(NormalizeVersion(minimum), out var minimumVersion)) return false;
        return actualVersion.CompareTo(minimumVersion) >= 0;
    }

    private static string NormalizeVersion(string value)
    {
        var match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        if (!match.Success) return value;

        var parts = match.Value.Split('.').ToList();
        while (parts.Count < 2) parts.Add("0");
        return string.Join('.', parts);
    }

    private static string? ExtractVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return null;
        var match = Regex.Match(versionText, @"\bversion\s+(?<version>\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static WorkerDto ToDto(WorkerEntity entity, IReadOnlyCollection<JobEntity> activeJobs)
    {
        WorkerCapabilitiesDto? capabilities = null;
        WorkerLocalStorageDto? localStorage = null;
        List<string> serverPrefixes = [];
        try
        {
            capabilities = JsonSerializer.Deserialize<WorkerCapabilitiesDto>(entity.CapabilitiesJson, JsonOptions);
        }
        catch
        {
            // Ignore malformed legacy capability JSON.
        }

        try
        {
            localStorage = JsonSerializer.Deserialize<WorkerLocalStorageDto>(entity.LocalStorageJson, JsonOptions);
        }
        catch
        {
            // Ignore malformed legacy local-storage JSON.
        }

        try
        {
            serverPrefixes = JsonSerializer.Deserialize<List<string>>(entity.ServerPrefixesJson, JsonOptions) ?? [];
        }
        catch
        {
            // Ignore malformed legacy path mapping JSON.
        }

        var active = activeJobs.Select(job => new ActiveWorkerJobDto
        {
            JobId = job.Id,
            JobType = job.JobType,
            Status = job.Status,
            LibraryId = job.LibraryId,
            MediaItemId = job.MediaItemId,
            Progress = job.Progress,
            Message = job.LastMessage,
            StartedUtc = job.LeaseStartedUtc,
            LastSeenUtc = job.LeaseLastSeenUtc
        }).ToList();

        var isIdle = active.Count == 0;
        return new WorkerDto
        {
            WorkerId = entity.WorkerId,
            WorkerName = entity.WorkerName,
            WorkerInstanceId = entity.WorkerInstanceId,
            WorkerVersion = entity.WorkerVersion,
            Roles = entity.Roles,
            State = entity.State,
            ControlState = entity.ControlState,
            RegisteredUtc = entity.RegisteredUtc,
            LastSeenUtc = entity.LastSeenUtc,
            MappingConfigHash = entity.MappingConfigHash,
            Capabilities = capabilities,
            ServerPrefixes = serverPrefixes,
            LocalStorage = localStorage,
            ActiveJobs = active,
            ActiveJobCount = active.Count,
            IsIdle = isIdle,
            SafeToStop = isIdle && entity.ControlState != WorkerControlState.Normal
        };
    }
}
