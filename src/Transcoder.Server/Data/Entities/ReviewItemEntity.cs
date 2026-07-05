using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class ReviewItemEntity
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public ReviewType ReviewType { get; set; }
    public ReviewSeverity Severity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public bool Resolved { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedUtc { get; set; }
}
