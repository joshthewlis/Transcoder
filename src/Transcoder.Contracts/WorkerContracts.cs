namespace Transcoder.Contracts;

public sealed class WorkerRegisterRequest
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string WorkerVersion { get; set; } = string.Empty;
    public WorkerRole Roles { get; set; } = WorkerRole.Prober;
    public WorkerCapabilitiesDto Capabilities { get; set; } = new();
    public WorkerLimitsDto Limits { get; set; } = new();
    public WorkerPathMappingSummaryDto PathMapping { get; set; } = new();
    public WorkerLocalStorageDto LocalStorage { get; set; } = new();
    public WorkerShutdownCapabilitiesDto Shutdown { get; set; } = new();
}

public sealed class WorkerRegisterResponse
{
    public bool Accepted { get; set; }
    public string ServerTimeUtc { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "1";
    public int ExpectedHeartbeatSeconds { get; set; }
    public int UnresponsiveAfterSeconds { get; set; }
    public int LostAfterSeconds { get; set; }
    public int PollSeconds { get; set; }
    public int MaxLeaseSeconds { get; set; }
    public bool PathCheckRequired { get; set; }
    public List<PathCheckDefinitionDto> PathChecks { get; set; } = [];
    public ProcessingMode ProcessingMode { get; set; }
    public WorkerControlState ControlState { get; set; }
    public string? Message { get; set; }
    public List<string> RequirementErrors { get; set; } = [];
}

public sealed class WorkerCapabilitiesDto
{
    public string? FfmpegVersion { get; set; }
    public string? FfprobeVersion { get; set; }
    public ToolCapabilityDto Ffmpeg { get; set; } = new() { Name = "ffmpeg" };
    public ToolCapabilityDto Ffprobe { get; set; } = new() { Name = "ffprobe" };
    public List<string> Encoders { get; set; } = [];
    public List<string> Decoders { get; set; } = [];
    public List<string> HwAccels { get; set; } = [];
    public bool AllowCpuEncoding { get; set; } = true;
    public bool AllowGpuEncoding { get; set; } = true;
    public List<string> CpuEncoders { get; set; } = [];
    public List<string> GpuEncoders { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ToolCapabilityDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Available { get; set; }
    public string? VersionText { get; set; }
    public string? Version { get; set; }
    public string? Error { get; set; }
}

public sealed class WorkerLimitsDto
{
    public int MaxProbeJobs { get; set; } = 1;
    public int MaxPlanReviewJobs { get; set; } = 1;
    public int MaxCleanupJobs { get; set; } = 4;
    public int MaxTranscodeJobs { get; set; } = 1;
    public int MaxValidationJobs { get; set; } = 1;
}

public sealed class WorkerPathMappingSummaryDto
{
    public string MappingConfigHash { get; set; } = string.Empty;
    public List<string> ServerPrefixes { get; set; } = [];
}


public sealed class WorkerLocalStorageDto
{
    public TranscodeWorkingMode WorkingMode { get; set; } = TranscodeWorkingMode.LocalThenCopyToStaging;
    public bool LocalWorkingRootExists { get; set; }
    public bool LocalWorkingRootWritable { get; set; }
    public bool LocalWorkingRootCreated { get; set; }
    public string? Error { get; set; }
}

public sealed class WorkerShutdownCapabilitiesDto
{
    public bool SupportsShutdown { get; set; }
    public bool SupportsExitOnly { get; set; } = true;
    public bool AllowRemoteShutdownRequest { get; set; }
    public bool AllowRemoteExitRequest { get; set; } = true;
}

public sealed class PathCheckDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public int? LibraryId { get; set; }
    public string ServerPath { get; set; } = string.Empty;
    public bool MustExist { get; set; } = true;
    public bool MustBeReadable { get; set; } = true;
    public bool MustBeWritable { get; set; }
    public bool AllowCreateIfMissing { get; set; }
}

public sealed class WorkerPathCheckResultsRequest
{
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string MappingConfigHash { get; set; } = string.Empty;
    public List<WorkerPathCheckResultDto> Results { get; set; } = [];
}

public sealed class WorkerPathCheckResultDto
{
    public string Id { get; set; } = string.Empty;
    public int? LibraryId { get; set; }
    public string ServerPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool Created { get; set; }
    public string? Error { get; set; }
}

public sealed class WorkerPathCheckDto
{
    public long Id { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string MappingConfigHash { get; set; } = string.Empty;
    public string CheckId { get; set; } = string.Empty;
    public int? LibraryId { get; set; }
    public string ServerPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool Created { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedUtc { get; set; }
}

public sealed class WorkerHeartbeatRequest
{
    public string WorkerInstanceId { get; set; } = string.Empty;
    public List<ActiveJobHeartbeatDto> ActiveJobs { get; set; } = [];
    public WorkerCapacityDto AvailableCapacity { get; set; } = new();
    public WorkerControlState? ControlStateApplied { get; set; }
    public string? ControlStateRejectedReason { get; set; }
}

public sealed class WorkerHeartbeatResponse
{
    public bool Accepted { get; set; }
    public WorkerControlState ControlState { get; set; }
    public bool AcceptNewWork { get; set; }
    public bool ShutdownWhenIdle { get; set; }
    public string ShutdownAction { get; set; } = "Disabled";
    public string? Message { get; set; }
    public bool PathCheckRequired { get; set; }
    public List<PathCheckDefinitionDto> PathChecks { get; set; } = [];
}

public sealed class ActiveJobHeartbeatDto
{
    public long JobId { get; set; }
    public string LeaseId { get; set; } = string.Empty;
    public JobType JobType { get; set; }
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public double? Fps { get; set; }
    public int? EtaSeconds { get; set; }
}

public sealed class WorkerCapacityDto
{
    public int Probe { get; set; }
    public int PlanReview { get; set; }
    public int Cleanup { get; set; }
    public int Transcode { get; set; }
    public int Validation { get; set; }
}

public sealed class WorkerDto
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string WorkerVersion { get; set; } = string.Empty;
    public WorkerRole Roles { get; set; }
    public WorkerState State { get; set; }
    public WorkerControlState ControlState { get; set; }
    public DateTime RegisteredUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? MappingConfigHash { get; set; }
    public WorkerCapabilitiesDto? Capabilities { get; set; }
    public List<string> ServerPrefixes { get; set; } = [];
    public WorkerLocalStorageDto? LocalStorage { get; set; }
    public List<ActiveWorkerJobDto> ActiveJobs { get; set; } = [];
    public int ActiveJobCount { get; set; }
    public bool IsIdle { get; set; }
    public bool SafeToStop { get; set; }
}

public sealed class ActiveWorkerJobDto
{
    public long JobId { get; set; }
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public long? MediaItemId { get; set; }
    public string? MediaName { get; set; }
    public string? MediaRelativePath { get; set; }
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}


public sealed class WorkerPathMappingDto
{
    public string ServerPrefix { get; set; } = string.Empty;
    public string WorkerPrefix { get; set; } = string.Empty;
}

public sealed class WorkerRuntimeSettingsDto
{
    public List<WorkerPathMappingDto> PathMappings { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}

public sealed class UpdateWorkerRuntimeSettingsRequest
{
    public List<WorkerPathMappingDto> PathMappings { get; set; } = [];
}

public sealed class SetWorkerControlStateRequest
{
    public WorkerControlState ControlState { get; set; }
    public string? Reason { get; set; }
}
