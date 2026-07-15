using System.Text.Json;

namespace Transcoder.Contracts;

public sealed class LeaseBatchRequest
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public List<JobLeaseRequestItemDto> Requests { get; set; } = [];
    public WorkerCapabilitiesDto Capabilities { get; set; } = new();
}

public sealed class JobLeaseRequestItemDto
{
    public JobType JobType { get; set; }
    public int MaxJobs { get; set; }
}

public sealed class JobLeaseBatchResponse
{
    public List<JobLeaseDto> Leases { get; set; } = [];
    public int RetryAfterSeconds { get; set; } = 10;
    public WorkerControlState ControlState { get; set; }
    public bool AcceptNewWork { get; set; } = true;
    public bool QueueEmptyForWorker { get; set; }
    public bool ServerHasAnyWork { get; set; }
    public bool ServerHasWorkForThisWorker { get; set; }
    public bool PathCheckRequired { get; set; }
    public bool StagedWorkPausedByActiveHours { get; set; }
    public ActiveHoursStatusDto? ActiveHours { get; set; }
    public List<PathCheckDefinitionDto> PathChecks { get; set; } = [];
    public string? Message { get; set; }
}

public sealed class JobLeaseDto
{
    public long JobId { get; set; }
    public string LeaseId { get; set; } = string.Empty;
    public JobType JobType { get; set; }
    public DateTime LeaseExpiresUtc { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed class JobProgressRequest
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string LeaseId { get; set; } = string.Empty;
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, string> Metrics { get; set; } = [];
}

public sealed class JobCompleteRequest
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string LeaseId { get; set; } = string.Empty;
    public JsonElement Result { get; set; }
}

public sealed class JobFailRequest
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string LeaseId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}

public sealed class JobDto
{
    public long Id { get; set; }
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public long? MediaItemId { get; set; }
    public string? MediaName { get; set; }
    public string? MediaRelativePath { get; set; }
    public string? MediaFullPath { get; set; }
    public string? RequiredEncoder { get; set; }
    public EncoderEngine RequiredEncoderEngine { get; set; } = EncoderEngine.Unknown;
    public string? LeaseId { get; set; }
    public string? LeasedByWorkerId { get; set; }
    public JobQueuePriority Priority { get; set; } = JobQueuePriority.Normal;
    public int AttemptNumber { get; set; }
    public int MaxAttempts { get; set; }
    public double? Progress { get; set; }
    public string? LastMessage { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}


public sealed class SetJobPriorityRequest
{
    public JobQueuePriority Priority { get; set; } = JobQueuePriority.High;
}
