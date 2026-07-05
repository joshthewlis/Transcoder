using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class JobEntity
{
    public long Id { get; set; }
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int? LibraryId { get; set; }
    public long? MediaItemId { get; set; }
    public string? RequiredEncoder { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? LeaseId { get; set; }
    public string? LastLeaseId { get; set; }
    public string? LeasedByWorkerId { get; set; }
    public string? LeasedByWorkerInstanceId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseStartedUtc { get; set; }
    public DateTime? LeaseLastSeenUtc { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int AttemptNumber { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public double? Progress { get; set; }
    public string? LastMessage { get; set; }
    public string? LastError { get; set; }
}
