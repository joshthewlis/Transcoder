namespace Transcoder.Server.Data.Entities;

public sealed class WorkerPathCheckEntity
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
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
}
