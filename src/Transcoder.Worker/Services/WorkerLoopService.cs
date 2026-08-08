using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class WorkerLoopService(
    IOptions<WorkerOptions> options,
    WorkerApiClient api,
    CapabilityDetector capabilities,
    PathMapper pathMapper,
    FfprobeRunner ffprobe,
    FfmpegRunner ffmpeg,
    ShutdownService shutdown,
    ILogger<WorkerLoopService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private readonly string _instanceId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private WorkerCapabilitiesDto _capabilities = new();
    private WorkerControlState _controlState = WorkerControlState.Normal;
    private bool _gamingGuardDraining;
    private DateTime? _gamingGuardGameSinceUtc;
    private DateTime? _gamingGuardClearSinceUtc;
    private DateTime _gamingGuardNextCheckUtc = DateTime.MinValue;
    private string? _gamingGuardLastReason;
    private int _pollSeconds = 10;
    private DateTime? _idleSinceUtc;
    private readonly ConcurrentDictionary<long, ActiveJobState> _activeJobs = new();
    private readonly SemaphoreSlim _sourceCopySlots = new(Math.Max(1, options.Value.SourceCopy.MaxConcurrentCopies), Math.Max(1, options.Value.SourceCopy.MaxConcurrentCopies));
    private readonly SemaphoreSlim _stagingCopySlots = new(Math.Max(1, options.Value.StagingCopy.MaxConcurrentCopies), Math.Max(1, options.Value.StagingCopy.MaxConcurrentCopies));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _capabilities = await capabilities.DetectAsync(stoppingToken);
        var requirementErrors = capabilities.ValidateLocalRequirements(_capabilities);
        if (requirementErrors.Count > 0)
        {
            foreach (var error in requirementErrors)
                logger.LogError("Worker requirement failed: {Error}", error);

            if (_options.StopIfRequiredToolsMissing)
            {
                logger.LogCritical("Worker will not register because required local tools are missing. Set Transcoder:StopIfRequiredToolsMissing=false to allow the server to reject/register it as degraded instead.");
                return;
            }
        }

        var registerResponse = await RegisterAsync(stoppingToken);
        if (!registerResponse.Accepted)
        {
            logger.LogCritical("Server rejected worker registration: {Message}. Errors: {Errors}", registerResponse.Message, string.Join(" | ", registerResponse.RequirementErrors));
            return;
        }

        _pollSeconds = registerResponse.PollSeconds <= 0 ? 10 : registerResponse.PollSeconds;
        _controlState = registerResponse.ControlState;

        if (await ApplyRuntimeSettingsAsync(stoppingToken))
            registerResponse.PathCheckRequired = true;

        if (registerResponse.PathCheckRequired)
            await RunPathChecksAsync(registerResponse.PathChecks, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var heartbeat = await SendHeartbeatAsync(stoppingToken);
                _controlState = heartbeat.ControlState;
                await EvaluateGamingGuardAsync(stoppingToken);

                if (await ApplyRuntimeSettingsAsync(stoppingToken))
                    heartbeat.PathCheckRequired = true;

                if (heartbeat.PathCheckRequired)
                    await RunPathChecksAsync(heartbeat.PathChecks, stoppingToken);

                if (heartbeat.AcceptNewWork && _controlState == WorkerControlState.Normal && !_gamingGuardDraining)
                {
                    var leaseResponse = await RequestAndStartWorkAsync(stoppingToken);
                    _controlState = leaseResponse.ControlState;
                    if (leaseResponse.RetryAfterSeconds > 0)
                        _pollSeconds = leaseResponse.RetryAfterSeconds;

                    if (leaseResponse.PathCheckRequired)
                        await RunPathChecksAsync(leaseResponse.PathChecks, stoppingToken);

                    TrackIdleState(leaseResponse.Leases.Count == 0 && _activeJobs.IsEmpty, leaseResponse.ControlState, stoppingToken);
                }
                else
                {
                    TrackIdleState(_activeJobs.IsEmpty, _controlState, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var retrySeconds = Math.Max(5, _pollSeconds);
                logger.LogError(ex, "Worker loop failed; keeping worker alive and retrying in {RetrySeconds} seconds.", retrySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
            }
        }
    }

    private async Task<bool> ApplyRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await api.GetRuntimeSettingsAsync(_options.WorkerId, cancellationToken);
            if (runtime is null)
                return false;
            if (runtime.UpdatedUtc == DateTime.MinValue && runtime.PathMappings.Count == 0)
                return false;

            return pathMapper.ApplyRuntimeSettings(runtime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not apply runtime worker settings from server.");
            return false;
        }
    }

    private async Task EvaluateGamingGuardAsync(CancellationToken cancellationToken)
    {
        if (!_options.GamingGuard.Enabled)
            return;

        var now = DateTime.UtcNow;
        var pollSeconds = Math.Max(5, _options.GamingGuard.PollSeconds);
        if (now < _gamingGuardNextCheckUtc)
            return;

        _gamingGuardNextCheckUtc = now.AddSeconds(pollSeconds);

        bool gameRunning;
        string? reason;
        try
        {
            (gameRunning, reason) = await DetectGameRunningAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gaming guard detection failed; leaving current drain state unchanged.");
            return;
        }

        if (gameRunning)
        {
            _gamingGuardClearSinceUtc = null;
            _gamingGuardGameSinceUtc ??= now;
            _gamingGuardLastReason = reason;

            var runningFor = now - _gamingGuardGameSinceUtc.Value;
            if (!_gamingGuardDraining && runningFor.TotalSeconds >= Math.Max(1, _options.GamingGuard.GameRunningSecondsBeforeDrain))
            {
                _gamingGuardDraining = true;
                logger.LogWarning("Gaming guard entering drain mode after {Seconds:n0}s of game activity. Reason: {Reason}", runningFor.TotalSeconds, reason ?? "game detected");
            }
            return;
        }

        _gamingGuardGameSinceUtc = null;
        _gamingGuardClearSinceUtc ??= now;

        if (_gamingGuardDraining)
        {
            var clearFor = now - _gamingGuardClearSinceUtc.Value;
            if (clearFor.TotalSeconds >= Math.Max(1, _options.GamingGuard.NoGameSecondsBeforeResume))
            {
                _gamingGuardDraining = false;
                _gamingGuardLastReason = null;
                logger.LogInformation("Gaming guard leaving drain mode after {Seconds:n0}s with no game detected.", clearFor.TotalSeconds);
            }
        }
    }

    private async Task<(bool Running, string? Reason)> DetectGameRunningAsync(CancellationToken cancellationToken)
    {
        var command = _options.GamingGuard.DetectionCommand;
        if (!string.IsNullOrWhiteSpace(command))
        {
            var exitCode = await RunDetectionCommandAsync(command, cancellationToken);
            if (exitCode == 0)
                return (true, $"detection command returned 0: {command}");
        }

        var processNames = _options.GamingGuard.ProcessNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (processNames.Count > 0)
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (processNames.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase) || name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                        return (true, $"process '{name}' matched GamingGuard.ProcessNames");
                }
                catch
                {
                    // Processes can exit while being inspected.
                }
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var linuxMatch = DetectLinuxSteamGameProcess();
            if (linuxMatch is not null)
                return (true, linuxMatch);
        }

        return (false, null);
    }

    private static async Task<int> RunDetectionCommandAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start gaming guard detection command.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private string? DetectLinuxSteamGameProcess()
    {
        var commandLineNeedles = _options.GamingGuard.CommandLineContains
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            var pidName = Path.GetFileName(procDir);
            if (!int.TryParse(pidName, out _))
                continue;

            try
            {
                var commPath = Path.Combine(procDir, "comm");
                var comm = File.Exists(commPath) ? File.ReadAllText(commPath).Trim() : string.Empty;
                if (comm.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                    comm.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                    comm.Equals("gamescope", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cmdlinePath = Path.Combine(procDir, "cmdline");
                var environPath = Path.Combine(procDir, "environ");
                var cmdline = File.Exists(cmdlinePath) ? File.ReadAllText(cmdlinePath).Replace('\0', ' ') : string.Empty;
                var environ = File.Exists(environPath) ? File.ReadAllText(environPath).Replace('\0', ' ') : string.Empty;
                var haystack = cmdline + " " + environ;

                if (haystack.Contains("SteamGameId=", StringComparison.OrdinalIgnoreCase) && !comm.Contains("steam", StringComparison.OrdinalIgnoreCase))
                    return $"Linux process {pidName}/{comm} has SteamGameId environment";

                foreach (var needle in commandLineNeedles)
                {
                    if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return $"Linux process {pidName}/{comm} matched '{needle}'";
                }
            }
            catch
            {
                // /proc entries may disappear or be unreadable; ignore and continue.
            }
        }

        return null;
    }

    private async Task<WorkerRegisterResponse> RegisterAsync(CancellationToken cancellationToken)
    {
        var request = new WorkerRegisterRequest
        {
            WorkerId = _options.WorkerId,
            WorkerName = _options.WorkerName,
            WorkerInstanceId = _instanceId,
            WorkerVersion = typeof(WorkerLoopService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Roles = _options.Roles,
            Capabilities = _capabilities,
            Limits = _options.Limits,
            PathMapping = new WorkerPathMappingSummaryDto
            {
                MappingConfigHash = pathMapper.MappingConfigHash,
                ServerPrefixes = pathMapper.ServerPrefixes.ToList()
            },
            LocalStorage = CheckLocalWorkingRoot(),
            Shutdown = new WorkerShutdownCapabilitiesDto
            {
                SupportsExitOnly = true,
                SupportsShutdown = !string.IsNullOrWhiteSpace(_options.Shutdown.ShutdownCommand),
                AllowRemoteExitRequest = _options.Shutdown.AllowRemoteExitRequest,
                AllowRemoteShutdownRequest = _options.Shutdown.AllowRemoteShutdownRequest
            }
        };

        logger.LogInformation("Registering worker {WorkerId} instance {InstanceId}", _options.WorkerId, _instanceId);
        return await api.RegisterAsync(request, cancellationToken);
    }

    private WorkerLocalStorageDto CheckLocalWorkingRoot()
    {
        try
        {
            var path = _options.LocalWorkingRoot;
            if (string.IsNullOrWhiteSpace(path))
            {
                return new WorkerLocalStorageDto
                {
                    WorkingMode = _options.TranscodeWorkingMode,
                    Error = "LocalWorkingRoot is not configured."
                };
            }

            var created = false;
            var exists = Directory.Exists(path);
            if (!exists && _options.AutoCreateLocalWorkingRoot)
            {
                var parent = Directory.GetParent(path);
                if (parent is not null && parent.Exists)
                {
                    Directory.CreateDirectory(path);
                    created = true;
                    exists = true;
                }
            }

            var writable = exists && CanWrite(path);
            return new WorkerLocalStorageDto
            {
                WorkingMode = _options.TranscodeWorkingMode,
                LocalWorkingRootExists = exists,
                LocalWorkingRootWritable = writable,
                LocalWorkingRootCreated = created,
                Error = writable ? null : $"Local working root failed checks. Exists={exists}, CanWrite={writable}, AutoCreate={_options.AutoCreateLocalWorkingRoot}"
            };
        }
        catch (Exception ex)
        {
            return new WorkerLocalStorageDto
            {
                WorkingMode = _options.TranscodeWorkingMode,
                Error = ex.Message
            };
        }
    }

    private async Task RunPathChecksAsync(List<PathCheckDefinitionDto> checks, CancellationToken cancellationToken)
    {
        var results = new List<WorkerPathCheckResultDto>();
        foreach (var check in checks)
        {
            results.Add(RunPathCheck(check));
        }

        await api.SendPathChecksAsync(_options.WorkerId, new WorkerPathCheckResultsRequest
        {
            WorkerInstanceId = _instanceId,
            MappingConfigHash = pathMapper.MappingConfigHash,
            Results = results
        }, cancellationToken);
    }

    private WorkerPathCheckResultDto RunPathCheck(PathCheckDefinitionDto check)
    {
        if (!pathMapper.TryMap(check.ServerPath, out var localPath))
        {
            return new WorkerPathCheckResultDto
            {
                Id = check.Id,
                LibraryId = check.LibraryId,
                ServerPath = check.ServerPath,
                Success = false,
                Error = "No worker path mapping matched the server path."
            };
        }

        try
        {
            var created = false;
            var exists = Directory.Exists(localPath) || File.Exists(localPath);

            if (!exists && check.AllowCreateIfMissing)
            {
                var parent = Directory.GetParent(localPath);
                if (parent is not null && parent.Exists)
                {
                    Directory.CreateDirectory(localPath);
                    created = true;
                    exists = true;
                }
            }

            var canRead = exists && CanRead(localPath);
            var canWrite = exists && (!check.MustBeWritable || CanWrite(localPath));
            var success = (!check.MustExist || exists) && (!check.MustBeReadable || canRead) && (!check.MustBeWritable || canWrite);
            return new WorkerPathCheckResultDto
            {
                Id = check.Id,
                LibraryId = check.LibraryId,
                ServerPath = check.ServerPath,
                Success = success,
                CanRead = canRead,
                CanWrite = canWrite,
                Created = created,
                Error = success ? null : $"Mapped path failed checks. Exists={exists}, CanRead={canRead}, CanWrite={canWrite}, AllowCreateIfMissing={check.AllowCreateIfMissing}"
            };
        }
        catch (Exception ex)
        {
            return new WorkerPathCheckResultDto
            {
                Id = check.Id,
                LibraryId = check.LibraryId,
                ServerPath = check.ServerPath,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static bool CanRead(string path)
    {
        if (File.Exists(path))
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.CanRead;
        }

        Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
        return true;
    }

    private static bool CanWrite(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var testPath = Path.Combine(directory, $".transcoder-write-test-{Guid.NewGuid():N}");
        File.WriteAllText(testPath, "test");
        File.Delete(testPath);
        return true;
    }

    private async Task<WorkerHeartbeatResponse> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        return await api.HeartbeatAsync(_options.WorkerId, new WorkerHeartbeatRequest
        {
            WorkerInstanceId = _instanceId,
            ActiveJobs = _activeJobs.Values.Select(job => new ActiveJobHeartbeatDto
            {
                JobId = job.JobId,
                LeaseId = job.LeaseId,
                JobType = job.JobType,
                Progress = job.Progress,
                Message = job.Message,
                Fps = job.Fps,
                EtaSeconds = job.EtaSeconds
            }).ToList(),
            AvailableCapacity = BuildAvailableCapacity()
        }, cancellationToken);
    }

    private async Task<JobLeaseBatchResponse> RequestAndStartWorkAsync(CancellationToken cancellationToken)
    {
        var request = new LeaseBatchRequest
        {
            WorkerId = _options.WorkerId,
            WorkerInstanceId = _instanceId,
            Capabilities = _capabilities,
            Requests = BuildLeaseRequests()
        };

        var response = await api.LeaseBatchAsync(request, cancellationToken);
        foreach (var lease in response.Leases)
        {
            var active = new ActiveJobState
            {
                JobId = lease.JobId,
                LeaseId = lease.LeaseId,
                JobType = lease.JobType,
                Progress = 0,
                Message = $"Starting {lease.JobType}"
            };

            if (!_activeJobs.TryAdd(lease.JobId, active))
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunJobAsync(lease, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal worker shutdown.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled job task failure for job {JobId}; worker will continue.", lease.JobId);
                    await TryReportJobFailureAsync(lease, ex, CancellationToken.None);
                }
                finally
                {
                    _activeJobs.TryRemove(lease.JobId, out _);
                }
            }, cancellationToken);
        }

        return response;
    }

    private WorkerCapacityDto BuildAvailableCapacity()
    {
        if (_gamingGuardDraining)
        {
            return new WorkerCapacityDto
            {
                Probe = 0,
                PlanReview = 0,
                Cleanup = 0,
                Transcode = 0,
                Validation = 0
            };
        }

        var stagingBacklogFull = IsStagingCopyBacklogFull();
        var localPipelineRemaining = GetLocalPipelineRemainingSlots();

        var cleanupCapacity = stagingBacklogFull
            ? 0
            : Math.Max(0, _options.Limits.MaxCleanupJobs - CountActive(JobType.Cleanup));
        var transcodeCapacity = stagingBacklogFull
            ? 0
            : Math.Max(0, _options.Limits.MaxTranscodeJobs - CountActive(JobType.Transcode));

        if (_options.SourceCopy.Enabled && localPipelineRemaining >= 0)
        {
            var cleanupLeaseSlots = Math.Min(cleanupCapacity, localPipelineRemaining);
            localPipelineRemaining -= cleanupLeaseSlots;

            cleanupCapacity = cleanupLeaseSlots;
            transcodeCapacity = Math.Min(transcodeCapacity, Math.Max(0, localPipelineRemaining));
        }

        return new WorkerCapacityDto
        {
            Probe = Math.Max(0, _options.Limits.MaxProbeJobs - CountActive(JobType.Probe)),
            PlanReview = Math.Max(0, _options.Limits.MaxPlanReviewJobs - CountActive(JobType.PlanReview)),
            Cleanup = cleanupCapacity,
            Transcode = transcodeCapacity,
            Validation = Math.Max(0, _options.Limits.MaxValidationJobs - CountActive(JobType.ValidateOutput))
        };
    }

    private int CountActive(JobType jobType) => _activeJobs.Values.Count(x => x.JobType == jobType);

    private int CountTranscodePipelineBacklog()
        => _activeJobs.Values.Count(x =>
            (x.JobType is JobType.Cleanup or JobType.Transcode) &&
            !x.WaitingForStagingCopy &&
            !x.CopyingToStaging);

    private int GetLocalPipelineRemainingSlots()
    {
        if (!_options.SourceCopy.Enabled)
            return -1;

        var maxBuffered = _options.SourceCopy.MaxBufferedLocalWorkItems;
        if (maxBuffered <= 0)
            return -1;

        return Math.Max(0, maxBuffered - CountTranscodePipelineBacklog());
    }

    private int CountStagingCopyBacklog()
        => _activeJobs.Values.Count(x => x.WaitingForStagingCopy || x.CopyingToStaging);

    private bool IsStagingCopyBacklogFull()
    {
        var maxBuffered = _options.StagingCopy.MaxBufferedLocalOutputs;
        return maxBuffered > 0 && CountStagingCopyBacklog() >= maxBuffered;
    }

    private List<JobLeaseRequestItemDto> BuildLeaseRequests()
    {
        var capacity = BuildAvailableCapacity();
        var requests = new List<JobLeaseRequestItemDto>();
        if (_options.Roles.HasFlag(WorkerRole.Prober))
        {
            requests.Add(new JobLeaseRequestItemDto { JobType = JobType.Probe, MaxJobs = capacity.Probe });
            requests.Add(new JobLeaseRequestItemDto { JobType = JobType.PlanReview, MaxJobs = capacity.PlanReview });
        }
        if (_options.Roles.HasFlag(WorkerRole.Cleanup))
            requests.Add(new JobLeaseRequestItemDto { JobType = JobType.Cleanup, MaxJobs = capacity.Cleanup });
        if (_options.Roles.HasFlag(WorkerRole.Transcoder))
            requests.Add(new JobLeaseRequestItemDto { JobType = JobType.Transcode, MaxJobs = capacity.Transcode });
        if (_options.Roles.HasFlag(WorkerRole.Validator))
            requests.Add(new JobLeaseRequestItemDto { JobType = JobType.ValidateOutput, MaxJobs = capacity.Validation });
        return requests.Where(x => x.MaxJobs > 0).ToList();
    }

    private async Task RunJobAsync(JobLeaseDto lease, CancellationToken cancellationToken)
    {
        try
        {
            UpdateActiveJob(lease.JobId, 0, $"Running {lease.JobType}");
            logger.LogInformation("Starting {JobType} job {JobId}", lease.JobType, lease.JobId);
            switch (lease.JobType)
            {
                case JobType.Probe:
                    await RunProbeJobAsync(lease, cancellationToken);
                    break;
                case JobType.PlanReview:
                    await RunPlanReviewJobAsync(lease, cancellationToken);
                    break;
                case JobType.Cleanup:
                    await RunTranscodeJobAsync(lease, cancellationToken);
                    break;
                case JobType.Transcode:
                    await RunTranscodeJobAsync(lease, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Worker job type {lease.JobType} is not implemented in this worker.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", lease.JobId);
            await TryReportJobFailureAsync(lease, ex, cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken);
        }
    }

    private async Task TryReportJobFailureAsync(JobLeaseDto lease, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            await api.FailJobAsync(lease.JobId, new JobFailRequest
            {
                WorkerId = _options.WorkerId,
                WorkerInstanceId = _instanceId,
                LeaseId = lease.LeaseId,
                ErrorCode = GetFailureCode(ex),
                Message = ex.Message,
                Details = ex.ToString()
            }, cancellationToken);
        }
        catch (Exception reportEx)
        {
            logger.LogError(reportEx, "Could not report failure for job {JobId}; worker will continue.", lease.JobId);
        }
    }

    private static string GetFailureCode(Exception ex)
    {
        if (ex is FfmpegException ffmpegException && !string.IsNullOrWhiteSpace(ffmpegException.ErrorCode))
            return ffmpegException.ErrorCode;

        return ex.GetType().Name;
    }

    private async Task RunProbeJobAsync(JobLeaseDto lease, CancellationToken cancellationToken)
    {
        UpdateActiveJob(lease.JobId, 0, "Probing file");
        var inputPath = lease.Payload.GetProperty("inputPath").GetString() ?? throw new InvalidOperationException("Probe payload missing inputPath.");
        logger.LogInformation("Probe started for {Path}", inputPath);
        if (!pathMapper.TryMap(inputPath, out var localPath))
            throw new InvalidOperationException($"No path mapping found for {inputPath}");

        var probeJson = await ffprobe.ProbeAsync(localPath, cancellationToken);
        var result = JsonSerializer.SerializeToElement(new
        {
            probeJson = JsonDocument.Parse(probeJson).RootElement,
            fileSizeBytes = new FileInfo(localPath).Length,
            lastModifiedUtc = File.GetLastWriteTimeUtc(localPath)
        }, _jsonOptions);

        logger.LogInformation("Probe complete for {Path}", inputPath);
        UpdateActiveJob(lease.JobId, 100, "Probe complete");
        await api.CompleteJobAsync(lease.JobId, new JobCompleteRequest
        {
            WorkerId = _options.WorkerId,
            WorkerInstanceId = _instanceId,
            LeaseId = lease.LeaseId,
            Result = result
        }, cancellationToken);
    }

    private async Task RunPlanReviewJobAsync(JobLeaseDto lease, CancellationToken cancellationToken)
    {
        UpdateActiveJob(lease.JobId, 0, "Reviewing plan");
        var inputPath = lease.Payload.GetProperty("inputPath").GetString() ?? throw new InvalidOperationException("PlanReview payload missing inputPath.");
        logger.LogInformation("Plan review started for {Path}", inputPath);
        if (!pathMapper.TryMap(inputPath, out var localPath))
            throw new InvalidOperationException($"No path mapping found for {inputPath}");

        var probeJson = await ffprobe.ProbeAsync(localPath, cancellationToken);
        using var probeDocument = JsonDocument.Parse(probeJson);
        var messages = new List<string>();
        var status = "Approved";

        if (lease.Payload.TryGetProperty("fileSizeBytes", out var expectedSizeElement) && expectedSizeElement.TryGetInt64(out var expectedSize))
        {
            var actualSize = new FileInfo(localPath).Length;
            if (actualSize != expectedSize)
            {
                status = "Rejected";
                messages.Add($"File size changed. Expected {expectedSize}, actual {actualSize}.");
            }
        }

        if (lease.Payload.TryGetProperty("lastModifiedUtc", out var expectedModifiedElement) && expectedModifiedElement.TryGetDateTime(out var expectedModified))
        {
            var actualModified = File.GetLastWriteTimeUtc(localPath);
            if (Math.Abs((actualModified - expectedModified).TotalSeconds) > 2)
            {
                status = "Rejected";
                messages.Add($"Last modified time changed. Expected {expectedModified:o}, actual {actualModified:o}.");
            }
        }

        if (lease.Payload.TryGetProperty("planJson", out var planElement) && planElement.TryGetProperty("streams", out var planStreams))
        {
            var availableIndexes = probeDocument.RootElement.GetProperty("streams").EnumerateArray()
                .Where(x => x.TryGetProperty("index", out _))
                .Select(x => x.GetProperty("index").GetInt32())
                .ToHashSet();

            foreach (var stream in planStreams.EnumerateArray())
            {
                if (!stream.TryGetProperty("sourceStreamIndex", out var indexElement))
                    continue;
                var index = indexElement.GetInt32();
                if (!availableIndexes.Contains(index))
                {
                    status = "Rejected";
                    messages.Add($"Planned stream index {index} does not exist in reprobe result.");
                }
            }
        }

        var result = JsonSerializer.SerializeToElement(new
        {
            reviewStatus = status,
            messages,
            reprobeJson = probeDocument.RootElement.Clone(),
            reprobedFileSizeBytes = new FileInfo(localPath).Length,
            reprobedLastModifiedUtc = File.GetLastWriteTimeUtc(localPath)
        }, _jsonOptions);

        logger.LogInformation("Plan review {Status} for {Path}", status, inputPath);
        UpdateActiveJob(lease.JobId, 100, $"Plan review {status}");
        await api.CompleteJobAsync(lease.JobId, new JobCompleteRequest
        {
            WorkerId = _options.WorkerId,
            WorkerInstanceId = _instanceId,
            LeaseId = lease.LeaseId,
            Result = result
        }, cancellationToken);
    }



    private async Task RunTranscodeJobAsync(JobLeaseDto lease, CancellationToken cancellationToken)
    {
        var operationName = lease.JobType == JobType.Cleanup ? "cleanup" : "transcode";
        UpdateActiveJob(lease.JobId, 0, $"Preparing {operationName}");

        var inputPath = lease.Payload.GetProperty("inputPath").GetString()
            ?? throw new InvalidOperationException("Transcode payload missing inputPath.");
        var stagingOutputPath = lease.Payload.GetProperty("stagingOutputPath").GetString()
            ?? throw new InvalidOperationException("Transcode payload missing stagingOutputPath.");

        logger.LogInformation("{Operation} started for {Path}", operationName, inputPath);

        if (!pathMapper.TryMap(inputPath, out var localInputPath))
            throw new InvalidOperationException($"No path mapping found for input path {inputPath}");
        if (!pathMapper.TryMap(stagingOutputPath, out var localStagingOutputPath))
            throw new InvalidOperationException($"No path mapping found for staging path {stagingOutputPath}");

        var ffmpegArgs = ReadStringArray(lease.Payload, "ffmpegArgs");
        if (ffmpegArgs.Count == 0 && lease.Payload.TryGetProperty("planJson", out var planElement))
            ffmpegArgs = ReadStringArray(planElement, "ffmpegArgs");
        if (ffmpegArgs.Count == 0)
            throw new InvalidOperationException("Transcode payload contains no ffmpeg arguments.");

        var jobWorkRoot = Path.Combine(_options.LocalWorkingRoot, $"job-{lease.JobId}", $"lease-{lease.LeaseId}");
        Directory.CreateDirectory(jobWorkRoot);

        var extension = Path.GetExtension(localStagingOutputPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".mkv";
        var localOutputPath = Path.Combine(jobWorkRoot, "output" + extension);
        var logPath = Path.Combine(jobWorkRoot, "ffmpeg.log");

        var originalInputFileSizeBytes = new FileInfo(localInputPath).Length;
        string? localSourceCopyPath = null;
        var ffmpegInputPath = localInputPath;

        try
        {
            if (_options.SourceCopy.Enabled)
            {
                localSourceCopyPath = await CopySourceToLocalAsync(lease, localInputPath, jobWorkRoot, cancellationToken);
                ffmpegInputPath = localSourceCopyPath;
            }

            var inputDurationSeconds = await GetInputDurationSecondsAsync(lease.Payload, ffmpegInputPath, cancellationToken);

            UpdateActiveJob(lease.JobId, 1, lease.JobType == JobType.Cleanup ? "Remuxing cleanup from local scratch" : "Encoding from local scratch");
            MarkSourceProcessingState(lease.JobId, processing: true);
            var runResult = await ffmpeg.RunAsync(
                ffmpegArgs,
                ffmpegInputPath,
                localOutputPath,
                (progress, message) => UpdateActiveJob(lease.JobId, progress, message),
                cancellationToken,
                inputDurationSeconds);
            MarkSourceProcessingState(lease.JobId, processing: false);

            await File.WriteAllTextAsync(logPath, runResult.LogText, cancellationToken);

            UpdateActiveJob(lease.JobId, 96, "Validating local output");
            await ffprobe.ProbeAsync(localOutputPath, cancellationToken);

            if (_options.SourceCopy.Enabled && _options.SourceCopy.DeleteLocalSourceAfterFfmpeg && localSourceCopyPath is not null)
            {
                TryDeleteFile(localSourceCopyPath);
                localSourceCopyPath = null;
            }

        var stagingDirectory = Path.GetDirectoryName(localStagingOutputPath);
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
            Directory.CreateDirectory(stagingDirectory);

        var stagingPartialPath = localStagingOutputPath + $".partial-{lease.LeaseId}";
        var localStagingCompleteMarkerPath = localStagingOutputPath + ".complete.json";
        var stagingCompleteMarkerPath = stagingOutputPath + ".complete.json";
        if (File.Exists(stagingPartialPath)) File.Delete(stagingPartialPath);

        await CopyLocalOutputToStagingAsync(
            lease,
            operationName,
            localOutputPath,
            stagingPartialPath,
            localStagingOutputPath,
            localStagingCompleteMarkerPath,
            cancellationToken);

            var outputFileSizeBytes = new FileInfo(localStagingOutputPath).Length;
            var inputFileSizeBytes = originalInputFileSizeBytes;
            var result = JsonSerializer.SerializeToElement(new
            {
                inputPath,
                stagingOutputPath,
                copiedSourceToLocal = _options.SourceCopy.Enabled,
                localOutputPath = _options.KeepLocalJobFilesOnSuccess ? localOutputPath : null,
                logPath = _options.KeepLocalJobFilesOnSuccess ? logPath : null,
                stagingCompleteMarkerPath,
                localStagingCompleteMarkerPath,
                stagingTransferComplete = true,
                exitCode = runResult.ExitCode,
                elapsedSeconds = runResult.Elapsed.TotalSeconds,
                outputFileSizeBytes,
                savedBytes = inputFileSizeBytes - outputFileSizeBytes,
                completedUtc = DateTime.UtcNow,
                jobType = lease.JobType.ToString(),
                operation = operationName,
                inputFileSizeBytes
            }, _jsonOptions);

            if (!_options.KeepLocalJobFilesOnSuccess)
            {
                TryDeleteDirectory(jobWorkRoot);
                TryDeleteEmptyParentDirectories(jobWorkRoot, _options.LocalWorkingRoot);
            }

            logger.LogInformation("{Operation} complete for {Path}. Output size {OutputSizeBytes} bytes, saved {SavedBytes} bytes.", operationName, inputPath, outputFileSizeBytes, inputFileSizeBytes - outputFileSizeBytes);
            UpdateActiveJob(lease.JobId, 100, lease.JobType == JobType.Cleanup ? "Cleanup staged" : "Transcode staged");
            await api.CompleteJobAsync(lease.JobId, new JobCompleteRequest
            {
                WorkerId = _options.WorkerId,
                WorkerInstanceId = _instanceId,
                LeaseId = lease.LeaseId,
                Result = result
            }, cancellationToken);
        }
        finally
        {
            MarkSourceProcessingState(lease.JobId, processing: false);
            if (localSourceCopyPath is not null && _options.SourceCopy.DeleteLocalSourceAfterFfmpeg)
                TryDeleteFile(localSourceCopyPath);
        }
    }


    private async Task<string> CopySourceToLocalAsync(
        JobLeaseDto lease,
        string localInputPath,
        string jobWorkRoot,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(localInputPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".input";
        var localSourcePath = Path.Combine(jobWorkRoot, "source" + extension);

        MarkSourceCopyState(lease.JobId, waiting: true, copying: false);
        UpdateActiveJob(lease.JobId, 1, $"Waiting for source copy slot ({CountTranscodePipelineBacklog()}/{Math.Max(1, _options.SourceCopy.MaxBufferedLocalWorkItems)} local work items)");

        await _sourceCopySlots.WaitAsync(cancellationToken);
        try
        {
            MarkSourceCopyState(lease.JobId, waiting: false, copying: true);
            UpdateActiveJob(lease.JobId, 2, "Copying source to local scratch");

            if (File.Exists(localSourcePath))
                File.Delete(localSourcePath);

            await CopyFileAsync(localInputPath, localSourcePath, cancellationToken);
            UpdateActiveJob(lease.JobId, 4, "Source copied to local scratch");
            return localSourcePath;
        }
        finally
        {
            MarkSourceCopyState(lease.JobId, waiting: false, copying: false);
            _sourceCopySlots.Release();
        }
    }

    private async Task CopyLocalOutputToStagingAsync(
        JobLeaseDto lease,
        string operationName,
        string localOutputPath,
        string stagingPartialPath,
        string localStagingOutputPath,
        string localStagingCompleteMarkerPath,
        CancellationToken cancellationToken)
    {
        MarkStagingCopyState(lease.JobId, waiting: true, copying: false);
        UpdateActiveJob(lease.JobId, 97, $"Waiting for staging copy slot ({CountStagingCopyBacklog()}/{Math.Max(1, _options.StagingCopy.MaxBufferedLocalOutputs)} local outputs waiting/copying)");

        await _stagingCopySlots.WaitAsync(cancellationToken);
        try
        {
            MarkStagingCopyState(lease.JobId, waiting: false, copying: true);

            if (File.Exists(stagingPartialPath))
                File.Delete(stagingPartialPath);

            UpdateActiveJob(lease.JobId, 98, $"Copying to staging ({CountStagingCopyBacklog()} local output(s) waiting/copying)");
            await CopyFileAsync(localOutputPath, stagingPartialPath, cancellationToken);

            UpdateActiveJob(lease.JobId, 99, "Validating staged partial output");
            await ffprobe.ProbeAsync(stagingPartialPath, cancellationToken);

            if (File.Exists(localStagingOutputPath))
                File.Delete(localStagingOutputPath);

            File.Move(stagingPartialPath, localStagingOutputPath);

            await File.WriteAllTextAsync(localStagingCompleteMarkerPath, JsonSerializer.Serialize(new
            {
                jobId = lease.JobId,
                leaseId = lease.LeaseId,
                jobType = lease.JobType.ToString(),
                operation = operationName,
                completedUtc = DateTime.UtcNow,
                outputFileSizeBytes = new FileInfo(localStagingOutputPath).Length
            }, _jsonOptions), cancellationToken);
        }
        finally
        {
            MarkStagingCopyState(lease.JobId, waiting: false, copying: false);
            _stagingCopySlots.Release();
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await source.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    private void MarkSourceCopyState(long jobId, bool waiting, bool copying)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            active.WaitingForSourceCopy = waiting;
            active.CopyingSourceToLocal = copying;
        }
    }

    private void MarkSourceProcessingState(long jobId, bool processing)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
            active.ProcessingLocalSource = processing;
    }

    private void MarkStagingCopyState(long jobId, bool waiting, bool copying)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            active.WaitingForStagingCopy = waiting;
            active.CopyingToStaging = copying;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup. Job failure/success should not be masked by scratch cleanup.
        }
    }

    private async Task<double?> GetInputDurationSecondsAsync(JsonElement payload, string localInputPath, CancellationToken cancellationToken)
    {
        var payloadDuration = TryReadDurationSeconds(payload);
        if (payloadDuration is > 0)
            return payloadDuration;

        try
        {
            var probeJson = await ffprobe.ProbeAsync(localInputPath, cancellationToken);
            return TryReadDurationSeconds(probeJson);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read input duration for ffmpeg progress from {Path}.", localInputPath);
            return null;
        }
    }

    private static double? TryReadDurationSeconds(string probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(probeJson);
            return TryReadDurationSeconds(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static double? TryReadDurationSeconds(JsonElement element)
    {
        if (TryGetPositiveDouble(element, "durationSeconds", out var directDuration))
            return directDuration;

        if (element.TryGetProperty("format", out var format) && TryGetPositiveDouble(format, "duration", out var formatDuration))
            return formatDuration;

        if (element.TryGetProperty("probeJson", out var probeJson))
        {
            var probeDuration = probeJson.ValueKind == JsonValueKind.String
                ? TryReadDurationSeconds(probeJson.GetString() ?? string.Empty)
                : TryReadDurationSeconds(probeJson);

            if (probeDuration is > 0)
                return probeDuration;
        }

        if (element.TryGetProperty("planJson", out var planJson) && TryReadDurationSeconds(planJson) is { } planDuration)
            return planDuration;

        if (element.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            var streamDurations = streams.EnumerateArray()
                .Select(stream => TryGetPositiveDouble(stream, "duration", out var duration) ? duration : (double?)null)
                .Where(duration => duration is > 0)
                .Select(duration => duration!.Value)
                .ToList();

            if (streamDurations.Count > 0)
                return streamDurations.Max();
        }

        return null;
    }

    private static bool TryGetPositiveDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            return value > 0;

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return value > 0;

        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Keep failed cleanup silent; stale scratch is preferable to failing a completed job report.
        }
    }

    private static void TryDeleteEmptyParentDirectories(string childPath, string stopAtRoot)
    {
        try
        {
            var root = Path.GetFullPath(stopAtRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directory = Directory.GetParent(Path.GetFullPath(childPath));

            while (directory is not null)
            {
                var current = directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                    break;

                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    break;

                Directory.Delete(current, recursive: false);
                directory = directory.Parent;
            }
        }
        catch
        {
            // Empty folder cleanup is best-effort only.
        }
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }


    private void UpdateActiveJob(long jobId, double? progress, string message)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            if (progress is not null)
                active.Progress = progress;
            active.Message = message;
        }
    }

    private void TrackIdleState(bool idle, WorkerControlState state, CancellationToken cancellationToken)
    {
        if (!idle)
        {
            _idleSinceUtc = null;
            return;
        }

        _idleSinceUtc ??= DateTime.UtcNow;
        var idleFor = DateTime.UtcNow - _idleSinceUtc.Value;
        if (idleFor.TotalSeconds < _options.Shutdown.IdleSecondsBeforeAction)
            return;

        if (state is WorkerControlState.DrainThenExit or WorkerControlState.DrainThenShutdown)
        {
            _ = shutdown.ApplyAsync(state, cancellationToken);
        }
    }

    private sealed class ActiveJobState
    {
        public long JobId { get; init; }
        public string LeaseId { get; init; } = string.Empty;
        public JobType JobType { get; init; }
        public double? Progress { get; set; }
        public string? Message { get; set; }
        public double? Fps { get; set; }
        public int? EtaSeconds { get; set; }
        public bool WaitingForSourceCopy { get; set; }
        public bool CopyingSourceToLocal { get; set; }
        public bool ProcessingLocalSource { get; set; }
        public bool WaitingForStagingCopy { get; set; }
        public bool CopyingToStaging { get; set; }
    }
}
