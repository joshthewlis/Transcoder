using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class WorkerEntity
{
    public int Id { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string WorkerInstanceId { get; set; } = string.Empty;
    public string WorkerVersion { get; set; } = string.Empty;
    public WorkerRole Roles { get; set; }
    public WorkerState State { get; set; } = WorkerState.Registered;
    public WorkerControlState ControlState { get; set; } = WorkerControlState.Normal;
    public DateTime RegisteredUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public string MappingConfigHash { get; set; } = string.Empty;
    public string CapabilitiesJson { get; set; } = "{}";
    public string LimitsJson { get; set; } = "{}";
    public string LocalStorageJson { get; set; } = "{}";
    public string ShutdownJson { get; set; } = "{}";
    public string ServerPrefixesJson { get; set; } = "[]";
    public string? LastControlReason { get; set; }
}
