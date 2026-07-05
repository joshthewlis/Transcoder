namespace Transcoder.Contracts;

public enum IntegrationType
{
    Radarr,
    Sonarr,
    Tmdb,
    Imdb
}

public sealed class IntegrationDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public IntegrationType IntegrationType { get; set; }
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public List<IntegrationPathMappingDto> PathMappings { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class CreateIntegrationRequest
{
    public string Name { get; set; } = string.Empty;
    public IntegrationType IntegrationType { get; set; }
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public List<IntegrationPathMappingDto> PathMappings { get; set; } = [];
}

public sealed class UpdateIntegrationRequest : CreateIntegrationRequest
{
}

public sealed class IntegrationPathMappingDto
{
    public string IntegrationPath { get; set; } = string.Empty;
    public string ServerPath { get; set; } = string.Empty;
}

public sealed class TestIntegrationResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Version { get; set; }
}

public sealed class MetadataRefreshResultDto
{
    public int LibraryId { get; set; }
    public long? MediaId { get; set; }
    public int MatchedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Messages { get; set; } = [];
}
