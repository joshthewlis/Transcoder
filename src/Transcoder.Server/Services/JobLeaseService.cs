using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class JobLeaseService(
    TranscoderDbContext db,
    SystemSettingsService settings,
    PathCheckDefinitionService pathChecks,
    IOptions<WorkerTimingOptions> timingOptions,
    IOptions<TranscoderServerOptions> serverOptions,
    IOptions<StorageOptions> storageOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<JobLeaseBatchResponse> LeaseBatchAsync(LeaseBatchRequest request, CancellationToken cancellationToken = default)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.WorkerId == request.WorkerId, cancellationToken);
        if (worker is null)
        {
            return new JobLeaseBatchResponse
            {
                RetryAfterSeconds = serverOptions.Value.PollSeconds,
                ControlState = WorkerControlState.Disabled,
                AcceptNewWork = false
            };
        }

        worker.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var response = BuildBaseResponse(worker);
        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
        var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);
        response.ActiveHours = activeHours;
        if (worker.State == WorkerState.PathCheckRequired)
        {
            response.AcceptNewWork = false;
            response.QueueEmptyForWorker = true;
            response.ServerHasAnyWork = await db.Jobs.AnyAsync(x => x.Status == JobStatus.Queued, cancellationToken);
            response.ServerHasWorkForThisWorker = false;
            response.PathCheckRequired = true;
            response.PathChecks = await pathChecks.BuildPathChecksAsync(cancellationToken);
            response.Message = "Worker path checks are required before leasing more jobs.";
            return response;
        }

        if (!CanAcceptWork(worker.ControlState, worker.State) || mode == ProcessingMode.Disabled)
        {
            response.QueueEmptyForWorker = true;
            response.ServerHasAnyWork = await db.Jobs.AnyAsync(x => x.Status == JobStatus.Queued, cancellationToken);
            response.ServerHasWorkForThisWorker = false;
            return response;
        }

        var leases = new List<JobLeaseDto>();
        var now = DateTime.UtcNow;
        var timing = timingOptions.Value;
        var workerLimits = DeserializeWorkerLimits(worker.LimitsJson);
        var activeCleanupJobs = await db.Jobs.CountAsync(j =>
            j.LeasedByWorkerId == worker.WorkerId
            && (j.Status == JobStatus.Leased || j.Status == JobStatus.Running)
            && j.JobType == JobType.Cleanup, cancellationToken);

        var activeTranscodeJobs = await db.Jobs.CountAsync(j =>
            j.LeasedByWorkerId == worker.WorkerId
            && (j.Status == JobStatus.Leased || j.Status == JobStatus.Running)
            && j.JobType == JobType.Transcode, cancellationToken);

        var storageAwareScheduling = storageOptions.Value.StorageAwareScheduling;
        var activeStorageCounts = storageAwareScheduling
            ? await GetActiveStorageCountsAsync(cancellationToken)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var leasedStorageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var stagedWorkPausedByActiveHours = false;

        foreach (var item in request.Requests.Where(x => x.MaxJobs > 0))
        {
            if (!IsJobTypeAllowedByMode(item.JobType, mode))
                continue;

            if (ActiveHoursEvaluator.AppliesToJobType(item.JobType) && !activeHours.AllowStagedWork)
            {
                stagedWorkPausedByActiveHours = true;
                continue;
            }

            var candidates = await GetLeaseCandidatesAsync(item.JobType, item.MaxJobs, activeStorageCounts, cancellationToken);
            var candidateStorageKeys = storageAwareScheduling && IsSourceWorkJob(item.JobType)
                ? await GetStorageKeysForJobsAsync(candidates, cancellationToken)
                : new Dictionary<long, string>();

            foreach (var job in candidates)
            {
                if (leases.Count(x => x.JobType == item.JobType) >= item.MaxJobs)
                    break;

                if (item.JobType == JobType.Cleanup && activeCleanupJobs + leases.Count(x => x.JobType == JobType.Cleanup) >= workerLimits.MaxCleanupJobs)
                    break;

                if (item.JobType == JobType.Transcode && activeTranscodeJobs + leases.Count(x => x.JobType == JobType.Transcode) >= workerLimits.MaxTranscodeJobs)
                    break;

                if (!WorkerCanRunJob(worker, request.Capabilities, job))
                    continue;

                var storageKey = storageAwareScheduling && IsSourceWorkJob(job.JobType)
                    ? candidateStorageKeys.GetValueOrDefault(job.Id, "unknown")
                    : null;

                if (!CanLeaseStorageKey(storageKey, activeStorageCounts, leasedStorageCounts))
                    continue;

                var leaseId = Guid.NewGuid().ToString("N");
                job.Status = JobStatus.Leased;
                job.LeaseId = leaseId;
                job.LastLeaseId = leaseId;
                job.LeasedByWorkerId = worker.WorkerId;
                job.LeasedByWorkerInstanceId = request.WorkerInstanceId;
                job.LeaseStartedUtc = now;
                job.LeaseLastSeenUtc = now;
                job.LeaseExpiresUtc = now.AddSeconds(timing.MaxLeaseSeconds);
                job.AttemptNumber++;

                leases.Add(new JobLeaseDto
                {
                    JobId = job.Id,
                    LeaseId = leaseId,
                    JobType = job.JobType,
                    LeaseExpiresUtc = job.LeaseExpiresUtc.Value,
                    Payload = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(job.PayloadJson, JsonOptions)
                });

                if (!string.IsNullOrWhiteSpace(storageKey))
                    leasedStorageCounts[storageKey] = leasedStorageCounts.GetValueOrDefault(storageKey) + 1;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        response.Leases = leases;
        response.QueueEmptyForWorker = leases.Count == 0;
        response.ServerHasAnyWork = await db.Jobs.AnyAsync(x => x.Status == JobStatus.Queued, cancellationToken);
        response.ServerHasWorkForThisWorker = leases.Count > 0;

        if (stagedWorkPausedByActiveHours && leases.Count == 0)
        {
            var queuedStagedWorkExists = await db.Jobs.AnyAsync(x =>
                x.Status == JobStatus.Queued &&
                (x.JobType == JobType.Cleanup || x.JobType == JobType.Transcode || x.JobType == JobType.ValidateOutput), cancellationToken);

            if (queuedStagedWorkExists)
            {
                response.StagedWorkPausedByActiveHours = true;
                response.Message = activeHours.Message;
                response.RetryAfterSeconds = Math.Max(response.RetryAfterSeconds, 60);
            }
        }

        if (leases.Count == 0 && !response.StagedWorkPausedByActiveHours && await MissingPathChecksForQueuedWorkAsync(worker, request, mode, cancellationToken))
        {
            worker.State = WorkerState.PathCheckRequired;
            await db.SaveChangesAsync(cancellationToken);
            response.AcceptNewWork = false;
            response.PathCheckRequired = true;
            response.PathChecks = await pathChecks.BuildPathChecksAsync(cancellationToken);
            response.Message = "Queued work exists, but this worker is missing path-check results for one or more libraries.";
        }

        return response;
    }

    private async Task<List<JobEntity>> GetLeaseCandidatesAsync(
        JobType jobType,
        int maxJobs,
        IReadOnlyDictionary<string, int> activeStorageCounts,
        CancellationToken cancellationToken)
    {
        var take = Math.Max(maxJobs * 200, 1000);
        var candidates = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued && j.JobType == jobType)
            .OrderBy(j => j.CreatedUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (!storageOptions.Value.StorageAwareScheduling || !IsSourceWorkJob(jobType) || candidates.Count <= 1)
            return candidates;

        await StorageMapImportService.EnsureTableAsync(db, cancellationToken);
        var storageKeys = await GetStorageKeysForJobsAsync(candidates, cancellationToken);

        return candidates
            .OrderBy(j => storageOptions.Value.PreferLeastBusyStorageKey ? activeStorageCounts.GetValueOrDefault(storageKeys.GetValueOrDefault(j.Id, "unknown")) : 0)
            .ThenBy(j => storageKeys.GetValueOrDefault(j.Id, "unknown"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(j => j.CreatedUtc)
            .ToList();
    }

    private async Task<Dictionary<string, int>> GetActiveStorageCountsAsync(CancellationToken cancellationToken)
    {
        if (!storageOptions.Value.StorageAwareScheduling)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await StorageMapImportService.EnsureTableAsync(db, cancellationToken);

        var activeJobs = await db.Jobs.AsNoTracking()
            .Where(j => j.MediaItemId != null
                && (j.JobType == JobType.Cleanup || j.JobType == JobType.Transcode)
                && (j.Status == JobStatus.Leased || j.Status == JobStatus.Running))
            .ToListAsync(cancellationToken);

        var storageKeys = await GetStorageKeysForJobsAsync(activeJobs, cancellationToken);
        return storageKeys.Values
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<long, string>> GetStorageKeysForJobsAsync(IReadOnlyCollection<JobEntity> jobs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        if (jobs.Count == 0)
            return result;

        var mediaIds = jobs
            .Where(x => x.MediaItemId is not null)
            .Select(x => x.MediaItemId!.Value)
            .Distinct()
            .ToList();

        if (mediaIds.Count == 0)
        {
            foreach (var job in jobs)
                result[job.Id] = "unknown";
            return result;
        }

        var mediaById = await db.MediaItems.AsNoTracking()
            .Where(x => mediaIds.Contains(x.Id))
            .Select(x => new { x.Id, x.RelativePath })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var storageByMediaId = await QueryMediaStorageAsync(mediaIds, cancellationToken);

        foreach (var job in jobs)
        {
            if (job.MediaItemId is null || !mediaById.TryGetValue(job.MediaItemId.Value, out var media))
            {
                result[job.Id] = "unknown";
                continue;
            }

            if (storageByMediaId.TryGetValue(media.Id, out var storage)
                && !storage.Stale
                && !string.IsNullOrWhiteSpace(storage.StorageKey))
            {
                result[job.Id] = storage.StorageKey!;
                continue;
            }

            result[job.Id] = storageOptions.Value.UseParentFolderForUnknownStorageKey
                ? StorageMapImportService.BuildFallbackStorageKey(media.RelativePath, storageOptions.Value.UnknownStorageKeyFolderDepth)
                : "unknown";
        }

        return result;
    }

    private async Task<Dictionary<long, MediaStorageLookup>> QueryMediaStorageAsync(IReadOnlyCollection<long> mediaIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, MediaStorageLookup>();
        if (mediaIds.Count == 0)
            return result;

        await StorageMapImportService.EnsureTableAsync(db, cancellationToken);

        var idList = string.Join(",", mediaIds.Distinct().Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"select MediaItemId, SourceStorageKey, SourceStorageStale from MediaStorage where MediaItemId in ({idList})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mediaItemId = reader.GetInt64(0);
                var storageKey = reader.IsDBNull(1) ? null : reader.GetString(1);
                var stale = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) != 0;
                result[mediaItemId] = new MediaStorageLookup(storageKey, stale);
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return result;
    }

    private bool CanLeaseStorageKey(string? storageKey, IReadOnlyDictionary<string, int> activeStorageCounts, IReadOnlyDictionary<string, int> leasedStorageCounts)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return true;

        if (!storageOptions.Value.StorageAwareScheduling || storageOptions.Value.MaxActiveSourceJobsPerStorageKey <= 0)
            return true;

        var active = activeStorageCounts.GetValueOrDefault(storageKey);
        var reserved = leasedStorageCounts.GetValueOrDefault(storageKey);
        return active + reserved < storageOptions.Value.MaxActiveSourceJobsPerStorageKey;
    }

    private static bool IsSourceWorkJob(JobType jobType) => jobType is JobType.Cleanup or JobType.Transcode;

    private sealed record MediaStorageLookup(string? StorageKey, bool Stale);

    private JobLeaseBatchResponse BuildBaseResponse(WorkerEntity worker)
    {
        var control = worker.ControlState;
        return new JobLeaseBatchResponse
        {
            RetryAfterSeconds = serverOptions.Value.PollSeconds,
            ControlState = control,
            AcceptNewWork = CanAcceptWork(control, worker.State)
        };
    }

    private static bool CanAcceptWork(WorkerControlState controlState, WorkerState workerState) =>
        controlState == WorkerControlState.Normal && (workerState == WorkerState.Online || workerState == WorkerState.PartialPathAccess);

    private static bool IsJobTypeAllowedByMode(JobType jobType, ProcessingMode mode) => mode switch
    {
        ProcessingMode.Disabled => false,
        ProcessingMode.ScanOnly => false,
        ProcessingMode.ProbeOnly => jobType == JobType.Probe,
        ProcessingMode.PlanOnly => jobType == JobType.Probe,
        ProcessingMode.PlanAndReview => jobType is JobType.Probe or JobType.PlanReview,
        ProcessingMode.TranscodeToStaging => true,
        ProcessingMode.ReplaceApproved => true,
        _ => false
    };

    private async Task<bool> MissingPathChecksForQueuedWorkAsync(WorkerEntity worker, LeaseBatchRequest request, ProcessingMode mode, CancellationToken cancellationToken)
    {
        var requestedTypes = request.Requests
            .Where(x => x.MaxJobs > 0 && IsJobTypeAllowedByMode(x.JobType, mode))
            .Select(x => x.JobType)
            .ToList();

        if (requestedTypes.Count == 0)
            return false;

        var queuedLibraryIds = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued && j.LibraryId != null && requestedTypes.Contains(j.JobType))
            .Select(j => j.LibraryId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var libraryId in queuedLibraryIds)
        {
            var checkId = $"library-{libraryId}-root";
            var hasAnyCurrentCheck = await db.WorkerPathChecks.AsNoTracking().AnyAsync(x =>
                x.WorkerId == worker.WorkerId
                && x.MappingConfigHash == worker.MappingConfigHash
                && x.CheckId == checkId, cancellationToken);

            if (!hasAnyCurrentCheck)
                return true;
        }

        return false;
    }

    private bool WorkerCanRunJob(WorkerEntity worker, WorkerCapabilitiesDto capabilities, JobEntity job)
    {
        if (!WorkerHasRole(worker.Roles, job.JobType))
            return false;

        if (job.JobType == JobType.Transcode && !WorkerCanSatisfyTranscodeRequirement(capabilities, job))
            return false;

        if (job.JobType != JobType.Transcode && !string.IsNullOrWhiteSpace(job.RequiredEncoder) && !capabilities.Encoders.Contains(job.RequiredEncoder, StringComparer.OrdinalIgnoreCase))
            return false;

        if (job.LibraryId is not null)
        {
            var libraryCheckId = $"library-{job.LibraryId}-root";
            var hasLibraryAccess = db.WorkerPathChecks.AsNoTracking().Any(x =>
                x.WorkerId == worker.WorkerId
                && x.MappingConfigHash == worker.MappingConfigHash
                && x.CheckId == libraryCheckId
                && x.Success
                && x.CanRead);

            if (!hasLibraryAccess)
                return false;
        }

        if (job.JobType is JobType.Probe or JobType.PlanReview)
            return true;

        var hasStagingAccess = db.WorkerPathChecks.AsNoTracking().Any(x =>
            x.WorkerId == worker.WorkerId
            && x.MappingConfigHash == worker.MappingConfigHash
            && x.CheckId == "transcoder-staging"
            && x.Success
            && (job.JobType == JobType.ValidateOutput ? x.CanRead : x.CanWrite));

        return job.JobType switch
        {
            JobType.Cleanup => hasStagingAccess && WorkerHasLocalWorkingAccess(worker),
            JobType.Transcode => hasStagingAccess && WorkerHasLocalWorkingAccess(worker),
            JobType.ValidateOutput => hasStagingAccess,
            _ => false
        };
    }


    private static bool WorkerCanSatisfyTranscodeRequirement(WorkerCapabilitiesDto capabilities, JobEntity job)
    {
        var requiredEncoder = job.RequiredEncoder;
        var requiredEngine = ReadRequiredEncoderEngine(job.PayloadJson, requiredEncoder);

        if (requiredEngine is EncoderEngine.Copy or EncoderEngine.Unknown or EncoderEngine.Either)
        {
            return string.IsNullOrWhiteSpace(requiredEncoder)
                || capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase);
        }

        if (requiredEngine == EncoderEngine.Cpu)
        {
            if (!capabilities.AllowCpuEncoding)
                return false;

            return string.IsNullOrWhiteSpace(requiredEncoder)
                ? capabilities.CpuEncoders.Count > 0
                : capabilities.CpuEncoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                    || (capabilities.CpuEncoders.Count == 0 && capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase) && InferEncoderEngine(requiredEncoder) == EncoderEngine.Cpu);
        }

        if (requiredEngine == EncoderEngine.Gpu)
        {
            if (!capabilities.AllowGpuEncoding)
                return false;

            return string.IsNullOrWhiteSpace(requiredEncoder)
                ? capabilities.GpuEncoders.Count > 0
                : capabilities.GpuEncoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                    || (capabilities.GpuEncoders.Count == 0 && capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase) && InferEncoderEngine(requiredEncoder) == EncoderEngine.Gpu);
        }

        return false;
    }

    private static EncoderEngine ReadRequiredEncoderEngine(string payloadJson, string? requiredEncoder)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
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
            // Legacy or malformed payload; fall back to encoder-name inference below.
        }

        return InferEncoderEngine(requiredEncoder);
    }

    private static EncoderEngine ReadEncoderEngine(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var engineElement))
            return EncoderEngine.Unknown;

        var value = engineElement.ValueKind == JsonValueKind.String ? engineElement.GetString() : engineElement.ToString();
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

    private static bool WorkerHasLocalWorkingAccess(WorkerEntity worker)
    {
        try
        {
            var localStorage = JsonSerializer.Deserialize<WorkerLocalStorageDto>(worker.LocalStorageJson, JsonOptions);
            return localStorage?.LocalWorkingRootExists == true && localStorage.LocalWorkingRootWritable;
        }
        catch
        {
            return false;
        }
    }

    private static WorkerLimitsDto DeserializeWorkerLimits(string json)
    {
        try { return JsonSerializer.Deserialize<WorkerLimitsDto>(json, JsonOptions) ?? new WorkerLimitsDto(); }
        catch { return new WorkerLimitsDto(); }
    }

    private static bool WorkerHasRole(WorkerRole roles, JobType jobType) => jobType switch
    {
        JobType.Probe => roles.HasFlag(WorkerRole.Prober),
        JobType.PlanReview => roles.HasFlag(WorkerRole.Prober),
        JobType.Cleanup => roles.HasFlag(WorkerRole.Transcoder),
        JobType.Transcode => roles.HasFlag(WorkerRole.Transcoder),
        JobType.ValidateOutput => roles.HasFlag(WorkerRole.Validator),
        _ => false
    };
}
