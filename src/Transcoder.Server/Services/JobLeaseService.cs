using System.Data;
using System.Globalization;
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
    ILogger<JobLeaseService> logger)
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
            logger.LogWarning(
                "Lease request rejected for unknown worker {WorkerId} instance {WorkerInstanceId}",
                request.WorkerId,
                request.WorkerInstanceId);

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

        var requestedWork = string.Join(", ", request.Requests
            .Where(x => x.MaxJobs > 0)
            .Select(x => $"{x.JobType} x{x.MaxJobs}"));

        logger.LogDebug(
            "Worker {WorkerId} asks for jobs. Instance={WorkerInstanceId}; Requests=[{Requests}]; State={WorkerState}; Control={ControlState}; Mode={ProcessingMode}",
            worker.WorkerId,
            request.WorkerInstanceId,
            requestedWork,
            worker.State,
            worker.ControlState,
            mode);

        if (worker.State == WorkerState.PathCheckRequired)
        {
            response.AcceptNewWork = false;
            response.QueueEmptyForWorker = true;
            response.ServerHasAnyWork = await db.Jobs.AnyAsync(x => x.Status == JobStatus.Queued, cancellationToken);
            response.ServerHasWorkForThisWorker = false;
            response.PathCheckRequired = true;
            response.PathChecks = await pathChecks.BuildPathChecksAsync(cancellationToken);
            response.Message = "Worker path checks are required before leasing more jobs.";
            logger.LogInformation(
                "Worker {WorkerId} could not lease work: path checks are required. ServerHasAnyWork={ServerHasAnyWork}",
                worker.WorkerId,
                response.ServerHasAnyWork);
            return response;
        }

        if (!CanAcceptWork(worker.ControlState, worker.State) || mode == ProcessingMode.Disabled)
        {
            response.QueueEmptyForWorker = true;
            response.ServerHasAnyWork = await db.Jobs.AnyAsync(x => x.Status == JobStatus.Queued, cancellationToken);
            response.ServerHasWorkForThisWorker = false;
            logger.LogInformation(
                "Worker {WorkerId} could not lease work: State={WorkerState}; Control={ControlState}; ProcessingMode={ProcessingMode}; ServerHasAnyWork={ServerHasAnyWork}",
                worker.WorkerId,
                worker.State,
                worker.ControlState,
                mode,
                response.ServerHasAnyWork);
            return response;
        }

        var leases = new List<JobLeaseDto>();
        var rejectionReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queuedCandidatesSeen = 0;
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

        var stagedWorkPausedByActiveHours = false;

        foreach (var item in request.Requests.Where(x => x.MaxJobs > 0))
        {
            if (!IsJobTypeAllowedByMode(item.JobType, mode))
            {
                AddRejection(rejectionReasons, $"{item.JobType}: processing mode {mode} does not allow this job type");
                continue;
            }

            if (ActiveHoursEvaluator.AppliesToJobType(item.JobType) && !activeHours.AllowStagedWork)
            {
                stagedWorkPausedByActiveHours = true;
                AddRejection(rejectionReasons, $"{item.JobType}: outside configured active hours");
                continue;
            }

            var candidateLimit = Math.Max(item.MaxJobs * 25, 100);
            var candidates = (await db.Jobs
                .Where(j => j.Status == JobStatus.Queued && j.JobType == item.JobType)
                .OrderBy(j => j.CreatedUtc)
                .Take(candidateLimit)
                .ToListAsync(cancellationToken))
                .OrderByDescending(j => JobPriorityHelper.ReadPriorityValue(j.PayloadJson))
                .ThenBy(j => j.CreatedUtc)
                .ToList();

            queuedCandidatesSeen += candidates.Count;
            if (candidates.Count == 0)
                AddRejection(rejectionReasons, $"{item.JobType}: no queued jobs");

            if (item.JobType is JobType.Cleanup or JobType.Transcode)
            {
                var storageSettings = await settings.GetStorageRuntimeSettingsAsync(cancellationToken);
                if (storageSettings.StorageAwareScheduling)
                {
                    var beforeStorageScheduling = candidates.Count;
                    candidates = await OrderCandidatesByStorageAsync(candidates, storageSettings, cancellationToken);
                    if (beforeStorageScheduling > 0 && candidates.Count == 0)
                        AddRejection(rejectionReasons, $"{item.JobType}: storage-aware scheduling currently has no available source slot");
                }
            }

            foreach (var job in candidates)
            {
                if (leases.Count(x => x.JobType == item.JobType) >= item.MaxJobs)
                    break;

                if (item.JobType == JobType.Cleanup && activeCleanupJobs + leases.Count(x => x.JobType == JobType.Cleanup) >= workerLimits.MaxCleanupJobs)
                {
                    AddRejection(rejectionReasons, $"Cleanup: worker capacity reached ({activeCleanupJobs}/{workerLimits.MaxCleanupJobs})");
                    break;
                }

                if (item.JobType == JobType.Transcode && activeTranscodeJobs + leases.Count(x => x.JobType == JobType.Transcode) >= workerLimits.MaxTranscodeJobs)
                {
                    AddRejection(rejectionReasons, $"Transcode: worker capacity reached ({activeTranscodeJobs}/{workerLimits.MaxTranscodeJobs})");
                    break;
                }

                var rejectionReason = GetWorkerJobRejectionReason(worker, request.Capabilities, job);
                if (rejectionReason is not null)
                {
                    AddRejection(rejectionReasons, $"{job.JobType}: {rejectionReason}");
                    continue;
                }

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

                logger.LogInformation(
                    "Job leased: JobId={JobId}; Type={JobType}; Worker={WorkerId}; Instance={WorkerInstanceId}; Attempt={AttemptNumber}; RequiredEncoder={RequiredEncoder}",
                    job.Id,
                    job.JobType,
                    worker.WorkerId,
                    request.WorkerInstanceId,
                    job.AttemptNumber,
                    job.RequiredEncoder ?? "(none)");
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
            AddRejection(rejectionReasons, "missing current path-check results for one or more queued libraries");
        }

        if (leases.Count == 0)
        {
            if (response.ServerHasAnyWork)
            {
                var reasonSummary = rejectionReasons.Count == 0
                    ? "queued work exists, but no eligible candidate was found in this worker's requested job types"
                    : string.Join("; ", rejectionReasons
                        .OrderByDescending(x => x.Value)
                        .Select(x => $"{x.Key} ({x.Value})"));

                logger.LogInformation(
                    "Worker {WorkerId} asked for jobs but could not lease one. CandidatesSeen={CandidatesSeen}; Reasons: {Reasons}",
                    worker.WorkerId,
                    queuedCandidatesSeen,
                    reasonSummary);
            }
            else
            {
                logger.LogDebug("Worker {WorkerId} asked for jobs; queue is empty.", worker.WorkerId);
            }
        }

        return response;
    }

    private async Task<List<JobEntity>> OrderCandidatesByStorageAsync(List<JobEntity> candidates, StorageRuntimeSettingsDto storageSettings, CancellationToken cancellationToken)
    {
        if (candidates.Count <= 1)
            return candidates;

        await StorageMapImportService.EnsureTableAsync(db, cancellationToken);

        var activeCounts = await ReadActiveStorageCountsAsync(cancellationToken);
        var keyed = new List<(JobEntity Job, string StorageKey, int ActiveCount, int Priority)>();
        foreach (var job in candidates)
        {
            var storageKey = job.MediaItemId is null
                ? "unknown"
                : await ReadStorageKeyForMediaAsync(job.MediaItemId.Value, storageSettings, cancellationToken);
            var activeCount = activeCounts.TryGetValue(storageKey, out var count) ? count : 0;
            keyed.Add((job, storageKey, activeCount, JobPriorityHelper.ReadPriorityValue(job.PayloadJson)));
        }

        var maxPerKey = Math.Max(1, storageSettings.MaxActiveSourceJobsPerStorageKey);
        var ordered = keyed
            .OrderBy(x => storageSettings.PreferLeastBusyStorageKey ? x.ActiveCount : 0)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.Job.CreatedUtc)
            .ToList();

        var selected = new List<JobEntity>();
        foreach (var item in ordered)
        {
            var current = activeCounts.TryGetValue(item.StorageKey, out var count) ? count : 0;
            if (current >= maxPerKey)
                continue;

            selected.Add(item.Job);
            activeCounts[item.StorageKey] = current + 1;
        }

        return selected;
    }

    private async Task<Dictionary<string, int>> ReadActiveStorageCountsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
select coalesce(nullif(ms.SourceStorageKey,''), 'unknown') as StorageKey, count(*) as ActiveCount
from Jobs j
left join MediaStorage ms on ms.MediaItemId = j.MediaItemId and coalesce(ms.SourceStorageStale,0) = 0
where j.Status in (1,2) and j.JobType in (2,4) and j.MediaItemId is not null
group by coalesce(nullif(ms.SourceStorageKey,''), 'unknown')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                var count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                result[key] = count;
            }
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }

        return result;
    }

    private async Task<string> ReadStorageKeyForMediaAsync(long mediaId, StorageRuntimeSettingsDto storageSettings, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
select m.RelativePath, ms.SourceStorageKey, coalesce(ms.SourceStorageStale, 1) as SourceStorageStale
from MediaItems m
left join MediaStorage ms on ms.MediaItemId = m.Id
where m.Id = $mediaId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$mediaId";
            parameter.Value = mediaId;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return "unknown";

            var relativePath = reader.IsDBNull(0) ? null : reader.GetString(0);
            var storageKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            var stale = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0;

            if (!stale && !string.IsNullOrWhiteSpace(storageKey))
                return storageKey!;

            return storageSettings.UseParentFolderForUnknownStorageKey
                ? StorageMapImportService.BuildFallbackStorageKey(relativePath, storageSettings.UnknownStorageKeyFolderDepth)
                : "unknown";
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

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

    private string? GetWorkerJobRejectionReason(WorkerEntity worker, WorkerCapabilitiesDto capabilities, JobEntity job)
    {
        if (!WorkerHasRole(worker.Roles, job.JobType))
            return $"worker does not advertise required role for {job.JobType}; Roles={worker.Roles}";

        if (job.JobType == JobType.Transcode)
        {
            var transcodeReason = GetTranscodeRequirementRejectionReason(capabilities, job);
            if (transcodeReason is not null)
                return transcodeReason;
        }
        else if (!string.IsNullOrWhiteSpace(job.RequiredEncoder)
                 && !capabilities.Encoders.Contains(job.RequiredEncoder, StringComparer.OrdinalIgnoreCase))
        {
            return $"required encoder '{job.RequiredEncoder}' is not advertised; Encoders=[{string.Join(", ", capabilities.Encoders)}]";
        }

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
                return $"library {job.LibraryId} read path has not passed its current path check";
        }

        if (job.JobType is JobType.Probe or JobType.PlanReview)
            return null;

        var stagingCheck = db.WorkerPathChecks.AsNoTracking()
            .Where(x =>
                x.WorkerId == worker.WorkerId
                && x.MappingConfigHash == worker.MappingConfigHash
                && x.CheckId == "transcoder-staging")
            .OrderByDescending(x => x.CheckedUtc)
            .FirstOrDefault();

        if (stagingCheck is null)
            return "staging path has no current path-check result";

        if (!stagingCheck.Success)
            return $"staging path check failed{FormatCheckError(stagingCheck.Error)}";

        if (job.JobType == JobType.ValidateOutput)
            return stagingCheck.CanRead ? null : "staging path is not readable";

        if (!stagingCheck.CanWrite)
            return "staging path is not writable";

        if (job.JobType is JobType.Cleanup or JobType.Transcode)
        {
            var localReason = GetLocalWorkingAccessRejectionReason(worker);
            if (localReason is not null)
                return localReason;
        }

        return job.JobType switch
        {
            JobType.Cleanup or JobType.Transcode or JobType.ValidateOutput => null,
            _ => $"unsupported job type {job.JobType}"
        };
    }

    private static string? GetTranscodeRequirementRejectionReason(WorkerCapabilitiesDto capabilities, JobEntity job)
    {
        var requiredEncoder = job.RequiredEncoder;
        var requiredEngine = ReadRequiredEncoderEngine(job.PayloadJson, requiredEncoder);

        if (requiredEngine is EncoderEngine.Copy or EncoderEngine.Unknown or EncoderEngine.Either)
        {
            if (string.IsNullOrWhiteSpace(requiredEncoder)
                || capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase))
                return null;

            return $"required encoder '{requiredEncoder}' is not advertised; Encoders=[{string.Join(", ", capabilities.Encoders)}]";
        }

        if (requiredEngine == EncoderEngine.Cpu)
        {
            if (!capabilities.AllowCpuEncoding)
                return $"job requires CPU encoding ({requiredEncoder ?? "any CPU encoder"}) but CPU encoding is disabled";

            if (string.IsNullOrWhiteSpace(requiredEncoder))
                return capabilities.CpuEncoders.Count > 0
                    ? null
                    : "job requires CPU encoding but worker advertises no CPU encoders";

            var supported = capabilities.CpuEncoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                || (capabilities.CpuEncoders.Count == 0
                    && capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                    && InferEncoderEngine(requiredEncoder) == EncoderEngine.Cpu);

            return supported
                ? null
                : $"required CPU encoder '{requiredEncoder}' is unavailable; CpuEncoders=[{string.Join(", ", capabilities.CpuEncoders)}]";
        }

        if (requiredEngine == EncoderEngine.Gpu)
        {
            if (!capabilities.AllowGpuEncoding)
                return $"job requires GPU encoding ({requiredEncoder ?? "any GPU encoder"}) but GPU encoding is disabled";

            if (string.IsNullOrWhiteSpace(requiredEncoder))
                return capabilities.GpuEncoders.Count > 0
                    ? null
                    : "job requires GPU encoding but worker advertises no GPU encoders";

            var supported = capabilities.GpuEncoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                || (capabilities.GpuEncoders.Count == 0
                    && capabilities.Encoders.Contains(requiredEncoder, StringComparer.OrdinalIgnoreCase)
                    && InferEncoderEngine(requiredEncoder) == EncoderEngine.Gpu);

            return supported
                ? null
                : $"required GPU encoder '{requiredEncoder}' is unavailable; GpuEncoders=[{string.Join(", ", capabilities.GpuEncoders)}]";
        }

        return $"unrecognized required encoder engine '{requiredEngine}'";
    }

    private static string? GetLocalWorkingAccessRejectionReason(WorkerEntity worker)
    {
        try
        {
            var localStorage = JsonSerializer.Deserialize<WorkerLocalStorageDto>(worker.LocalStorageJson, JsonOptions);
            if (localStorage is null)
                return "worker did not report local-working storage";

            if (!localStorage.LocalWorkingRootExists)
                return "local-working root does not exist";

            if (!localStorage.LocalWorkingRootWritable)
                return "local-working root is not writable";

            return null;
        }
        catch (Exception ex)
        {
            return $"worker local-storage report could not be read ({ex.GetType().Name})";
        }
    }

    private static string FormatCheckError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error}";

    private static void AddRejection(IDictionary<string, int> reasons, string reason)
    {
        if (reasons.TryGetValue(reason, out var count))
            reasons[reason] = count + 1;
        else
            reasons[reason] = 1;
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
        JobType.Cleanup => roles.HasFlag(WorkerRole.Cleanup),
        JobType.Transcode => roles.HasFlag(WorkerRole.Transcoder),
        JobType.ValidateOutput => roles.HasFlag(WorkerRole.Validator),
        _ => false
    };
}
