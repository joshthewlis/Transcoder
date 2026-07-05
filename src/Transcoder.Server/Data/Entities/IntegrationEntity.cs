using Transcoder.Contracts;

namespace Transcoder.Server.Data.Entities;

public sealed class IntegrationEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public IntegrationType IntegrationType { get; set; }
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string PathMappingsJson { get; set; } = "[]";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
