using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Transcoder.Contracts;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public sealed class IntegrationApiClient(IHttpClientFactory httpClientFactory, ILogger<IntegrationApiClient> logger)
{
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<TestIntegrationResultDto> TestAsync(IntegrationEntity integration, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var url = integration.IntegrationType switch
            {
                IntegrationType.Radarr => BuildUrl(integration, "/api/v3/system/status"),
                IntegrationType.Sonarr => BuildUrl(integration, "/api/v3/system/status"),
                IntegrationType.Tmdb => BuildUrl(integration, "/3/configuration", includeApiKey: true),
                IntegrationType.Imdb => integration.BaseUrl,
                _ => integration.BaseUrl
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request, integration);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new TestIntegrationResultDto { Success = false, Message = $"{(int)response.StatusCode} {response.ReasonPhrase}: {body[..Math.Min(250, body.Length)]}" };

            var version = TryGetString(body, "version") ?? TryGetString(body, "name");
            return new TestIntegrationResultDto { Success = true, Message = "Connection successful.", Version = version };
        }
        catch (Exception ex)
        {
            return new TestIntegrationResultDto { Success = false, Message = ex.Message };
        }
    }

    public Task<List<JsonElement>> GetRadarrMoviesAsync(IntegrationEntity integration, CancellationToken cancellationToken = default)
        => GetJsonArrayAsync($"radarr:{integration.Id}:movies", integration, "/api/v3/movie", cancellationToken);

    public Task<List<JsonElement>> GetSonarrSeriesAsync(IntegrationEntity integration, CancellationToken cancellationToken = default)
        => GetJsonArrayAsync($"sonarr:{integration.Id}:series", integration, "/api/v3/series", cancellationToken);

    public async Task<string?> GetTmdbMovieOriginalLanguageAsync(IntegrationEntity tmdb, int tmdbId, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"tmdb:{tmdb.Id}:movie:{tmdbId}", tmdb, $"/3/movie/{tmdbId}", cancellationToken, includeApiKey: true);
        return TryGetString(json, "original_language");
    }

    public async Task<string?> GetTmdbTvOriginalLanguageAsync(IntegrationEntity tmdb, int tmdbId, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"tmdb:{tmdb.Id}:tv:{tmdbId}", tmdb, $"/3/tv/{tmdbId}", cancellationToken, includeApiKey: true);
        return TryGetString(json, "original_language");
    }

    public async Task<int?> FindTmdbTvIdByTvdbIdAsync(IntegrationEntity tmdb, int tvdbId, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"tmdb:{tmdb.Id}:find:tvdb:{tvdbId}", tmdb, $"/3/find/{tvdbId}?external_source=tvdb_id", cancellationToken, includeApiKey: true);
        return TryGetFirstId(json, "tv_results");
    }

    public async Task<int?> FindTmdbMovieIdByImdbIdAsync(IntegrationEntity tmdb, string imdbId, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"tmdb:{tmdb.Id}:find:imdb:{imdbId}", tmdb, $"/3/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", cancellationToken, includeApiKey: true);
        return TryGetFirstId(json, "movie_results");
    }

    public async Task<int?> FindTmdbTvIdByImdbIdAsync(IntegrationEntity tmdb, string imdbId, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"tmdb:{tmdb.Id}:find:imdbtv:{imdbId}", tmdb, $"/3/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", cancellationToken, includeApiKey: true);
        return TryGetFirstId(json, "tv_results");
    }

    private async Task<List<JsonElement>> GetJsonArrayAsync(string cacheKey, IntegrationEntity integration, string path, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(cacheKey, integration, path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private async Task<string> GetJsonAsync(string cacheKey, IntegrationEntity integration, string path, CancellationToken cancellationToken, bool includeApiKey = false)
    {
        if (cache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.CreatedUtc < CacheTtl)
            return entry.Json;

        var client = httpClientFactory.CreateClient();
        var url = BuildUrl(integration, path, includeApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, integration);
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        cache[cacheKey] = new CacheEntry(DateTime.UtcNow, json);
        return json;
    }

    private static string BuildUrl(IntegrationEntity integration, string path, bool includeApiKey = false)
    {
        var baseUrl = string.IsNullOrWhiteSpace(integration.BaseUrl)
            ? integration.IntegrationType == IntegrationType.Tmdb ? "https://api.themoviedb.org" : string.Empty
            : integration.BaseUrl.TrimEnd('/');

        if (Uri.TryCreate(path, UriKind.Absolute, out _))
            return path;

        var url = $"{baseUrl}{path}";
        if (integration.IntegrationType is IntegrationType.Radarr or IntegrationType.Sonarr && !string.IsNullOrWhiteSpace(integration.ApiKey))
        {
            url += url.Contains('?') ? "&" : "?";
            url += $"apikey={Uri.EscapeDataString(integration.ApiKey)}";
        }
        else if (includeApiKey && integration.IntegrationType == IntegrationType.Tmdb && !LooksLikeBearerToken(integration.ApiKey) && !string.IsNullOrWhiteSpace(integration.ApiKey))
        {
            url += url.Contains('?') ? "&" : "?";
            url += $"api_key={Uri.EscapeDataString(integration.ApiKey)}";
        }

        return url;
    }

    private static void AddHeaders(HttpRequestMessage request, IntegrationEntity integration)
    {
        if (integration.IntegrationType is IntegrationType.Radarr or IntegrationType.Sonarr && !string.IsNullOrWhiteSpace(integration.ApiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", integration.ApiKey);

        if (integration.IntegrationType == IntegrationType.Tmdb && LooksLikeBearerToken(integration.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.ApiKey);
    }

    private static bool LooksLikeBearerToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length > 80;

    private static string? TryGetString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetString(document.RootElement, propertyName);
        }
        catch { return null; }
    }

    public static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static int? TryGetFirstId(string json, string arrayProperty)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(arrayProperty, out var results) || results.ValueKind != JsonValueKind.Array)
            return null;
        var first = results.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "id") : null;
    }

    private sealed record CacheEntry(DateTime CreatedUtc, string Json);
}
