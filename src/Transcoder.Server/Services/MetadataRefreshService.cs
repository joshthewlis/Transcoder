using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public sealed class MetadataRefreshService(
    TranscoderDbContext db,
    IntegrationApiClient integrationApi,
    ILogger<MetadataRefreshService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<MetadataRefreshResultDto> RefreshLibraryAsync(int libraryId, bool force = false, CancellationToken cancellationToken = default)
    {
        var library = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (library is null)
            return new MetadataRefreshResultDto { LibraryId = libraryId, Messages = ["Library not found."] };

        var result = new MetadataRefreshResultDto { LibraryId = libraryId };
        var ids = await db.MediaItems
            .Where(x => x.LibraryId == libraryId)
            .OrderBy(x => x.RelativePath)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var mediaId in ids)
        {
            var refreshed = await RefreshMediaAsync(mediaId, force, cancellationToken);
            result.MatchedCount += refreshed.MatchedCount;
            result.UpdatedCount += refreshed.UpdatedCount;
            result.FailedCount += refreshed.FailedCount;
            if (refreshed.Messages.Count > 0 && result.Messages.Count < 20)
                result.Messages.AddRange(refreshed.Messages.Take(20 - result.Messages.Count));
        }

        return result;
    }

    public async Task<MetadataRefreshResultDto> RefreshMediaAsync(long mediaId, bool force = false, CancellationToken cancellationToken = default)
    {
        var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media?.Library is null)
            return new MetadataRefreshResultDto { MediaId = mediaId, FailedCount = 1, Messages = ["Media item not found."] };

        var policy = DeserializePolicy(media.Library.PolicyJson);
        var matched = await TryRefreshTrackedMediaAsync(media, policy, force, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new MetadataRefreshResultDto
        {
            LibraryId = media.LibraryId,
            MediaId = media.Id,
            MatchedCount = matched ? 1 : 0,
            UpdatedCount = matched ? 1 : 0,
            FailedCount = matched ? 0 : 1,
            Messages = matched ? [] : [media.MetadataError ?? "No metadata match found."]
        };
    }

    public async Task<bool> TryRefreshTrackedMediaAsync(MediaItemEntity media, LibraryPolicyDto policy, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && !string.IsNullOrWhiteSpace(media.OriginalLanguage) && media.OriginalLanguageSource != MetadataSource.None)
            return true;

        media.MetadataRefreshedUtc = DateTime.UtcNow;
        media.MetadataError = null;

        foreach (var source in policy.Metadata.MetadataSourcePriority.DefaultIfEmpty(MetadataSource.Radarr))
        {
            try
            {
                var match = source switch
                {
                    MetadataSource.Radarr => await TryRadarrAsync(media, policy, cancellationToken),
                    MetadataSource.Sonarr => await TrySonarrAsync(media, policy, cancellationToken),
                    MetadataSource.Tmdb => await TryTmdbFromExistingMetadataAsync(media, policy, cancellationToken),
                    MetadataSource.FileProbe => TryInferFromProbe(media),
                    MetadataSource.LibraryDefault => TryLibraryDefault(policy),
                    _ => null
                };

                if (match?.OriginalLanguage is null)
                    continue;

                media.OriginalLanguage = LanguageNormalizer.Normalize(match.OriginalLanguage);
                media.OriginalLanguageSource = match.Source;
                media.MetadataMatchJson = JsonSerializer.Serialize(match, JsonOptions);
                media.MetadataError = null;
                media.UpdatedUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata source {Source} failed for media {MediaId}", source, media.Id);
                media.MetadataError = $"{source}: {ex.Message}";
            }
        }

        media.OriginalLanguage = null;
        media.OriginalLanguageSource = MetadataSource.None;
        media.UpdatedUtc = DateTime.UtcNow;
        media.MetadataError ??= "No metadata source could determine original language.";
        return false;
    }

    private async Task<MetadataMatch?> TryRadarrAsync(MediaItemEntity media, LibraryPolicyDto policy, CancellationToken cancellationToken)
    {
        var radarr = await GetIntegrationAsync(IntegrationType.Radarr, policy.Metadata.RadarrIntegrationId, cancellationToken);
        if (radarr is null)
            return null;

        var targetPath = MapServerPathToIntegrationPath(media.FullPath, radarr);
        var movies = await integrationApi.GetRadarrMoviesAsync(radarr, cancellationToken);
        var movie = movies.FirstOrDefault(x => RadarrMovieMatchesPath(x, targetPath));
        if (movie.ValueKind == JsonValueKind.Undefined)
            return null;

        var externalIds = ExtractRadarrIds(movie);

        // Prefer TMDb where Radarr has supplied the trusted TMDb ID.
        var tmdbId = IntegrationApiClient.TryGetInt(movie, "tmdbId");
        if (tmdbId is not null)
        {
            var tmdbLanguage = await TryGetTmdbMovieLanguageAsync(tmdbId.Value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(tmdbLanguage))
            {
                return new MetadataMatch
                {
                    Source = MetadataSource.Tmdb,
                    OriginalLanguage = tmdbLanguage,
                    IntegrationId = radarr.Id,
                    IntegrationName = radarr.Name,
                    MatchMethod = "RadarrPath→TMDb",
                    MatchedPath = targetPath,
                    ExternalIds = externalIds
                };
            }
        }

        var directLanguage = ExtractLanguage(movie);
        if (!string.IsNullOrWhiteSpace(directLanguage))
        {
            return new MetadataMatch
            {
                Source = MetadataSource.Radarr,
                OriginalLanguage = directLanguage,
                IntegrationId = radarr.Id,
                IntegrationName = radarr.Name,
                MatchMethod = "RadarrPath",
                MatchedPath = targetPath,
                ExternalIds = externalIds
            };
        }

        return null;
    }

    private async Task<MetadataMatch?> TrySonarrAsync(MediaItemEntity media, LibraryPolicyDto policy, CancellationToken cancellationToken)
    {
        var sonarr = await GetIntegrationAsync(IntegrationType.Sonarr, policy.Metadata.SonarrIntegrationId, cancellationToken);
        if (sonarr is null)
            return null;

        var targetPath = NormalizePathForCompare(MapServerPathToIntegrationPath(media.FullPath, sonarr));
        var series = await integrationApi.GetSonarrSeriesAsync(sonarr, cancellationToken);
        var best = series
            .Select(x => new { Series = x, Path = NormalizePathForCompare(IntegrationApiClient.TryGetString(x, "path")) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && targetPath.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Path.Length)
            .FirstOrDefault();

        if (best is null)
            return null;

        var externalIds = ExtractSonarrIds(best.Series);

        // Prefer TMDb when Sonarr gives us a trusted external ID. Sonarr can expose
        // language as an internal numeric ID, which should not be stored directly as
        // a media original language.
        var tmdbLanguage = await TryGetTmdbTvLanguageFromSonarrSeriesAsync(best.Series, cancellationToken);
        if (!string.IsNullOrWhiteSpace(tmdbLanguage))
        {
            return new MetadataMatch
            {
                Source = MetadataSource.Tmdb,
                OriginalLanguage = tmdbLanguage,
                IntegrationId = sonarr.Id,
                IntegrationName = sonarr.Name,
                MatchMethod = "SonarrSeriesPath→TMDb",
                MatchedPath = targetPath,
                ExternalIds = externalIds
            };
        }

        // Fall back to Sonarr's direct language metadata only after normalising known
        // shapes and numeric language IDs.
        var directLanguage = ExtractSonarrLanguage(best.Series);
        if (!string.IsNullOrWhiteSpace(directLanguage))
        {
            return new MetadataMatch
            {
                Source = MetadataSource.Sonarr,
                OriginalLanguage = directLanguage,
                IntegrationId = sonarr.Id,
                IntegrationName = sonarr.Name,
                MatchMethod = "SonarrSeriesPath",
                MatchedPath = targetPath,
                ExternalIds = externalIds
            };
        }

        return null;
    }

    private async Task<MetadataMatch?> TryTmdbFromExistingMetadataAsync(MediaItemEntity media, LibraryPolicyDto policy, CancellationToken cancellationToken)
    {
        if (!policy.Metadata.UseTmdb)
            return null;

        // Direct TMDb filename/title matching is intentionally not implemented yet because it can false-match large libraries.
        // TMDb is used here after Radarr/Sonarr have provided a trusted external ID.
        await Task.CompletedTask;
        return null;
    }

    private MetadataMatch? TryInferFromProbe(MediaItemEntity media)
    {
        if (string.IsNullOrWhiteSpace(media.ProbeJson))
            return null;

        var languages = ExtractAudioLanguages(media.ProbeJson);
        if (languages.Count != 1)
            return null;

        return new MetadataMatch
        {
            Source = MetadataSource.FileProbe,
            OriginalLanguage = languages.Single(),
            MatchMethod = "SingleAudioLanguage"
        };
    }

    private static MetadataMatch? TryLibraryDefault(LibraryPolicyDto policy)
    {
        var language = LanguageNormalizer.Normalize(policy.Metadata.DefaultOriginalLanguage);
        return string.IsNullOrWhiteSpace(language)
            ? null
            : new MetadataMatch { Source = MetadataSource.LibraryDefault, OriginalLanguage = language, MatchMethod = "LibraryDefault" };
    }

    private async Task<string?> TryGetTmdbMovieLanguageAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var tmdb = await GetIntegrationAsync(IntegrationType.Tmdb, null, cancellationToken);
        if (tmdb is null)
            return null;
        return LanguageNormalizer.Normalize(await integrationApi.GetTmdbMovieOriginalLanguageAsync(tmdb, tmdbId, cancellationToken));
    }

    private async Task<string?> TryGetTmdbTvLanguageFromSonarrSeriesAsync(JsonElement series, CancellationToken cancellationToken)
    {
        var tmdb = await GetIntegrationAsync(IntegrationType.Tmdb, null, cancellationToken);
        if (tmdb is null)
            return null;

        var tvdbId = IntegrationApiClient.TryGetInt(series, "tvdbId");
        if (tvdbId is not null)
        {
            var tmdbId = await integrationApi.FindTmdbTvIdByTvdbIdAsync(tmdb, tvdbId.Value, cancellationToken);
            if (tmdbId is not null)
                return LanguageNormalizer.Normalize(await integrationApi.GetTmdbTvOriginalLanguageAsync(tmdb, tmdbId.Value, cancellationToken));
        }

        var imdbId = IntegrationApiClient.TryGetString(series, "imdbId");
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var tmdbId = await integrationApi.FindTmdbTvIdByImdbIdAsync(tmdb, imdbId, cancellationToken);
            if (tmdbId is not null)
                return LanguageNormalizer.Normalize(await integrationApi.GetTmdbTvOriginalLanguageAsync(tmdb, tmdbId.Value, cancellationToken));
        }

        return null;
    }

    private async Task<IntegrationEntity?> GetIntegrationAsync(IntegrationType type, int? preferredId, CancellationToken cancellationToken)
    {
        var query = db.Integrations.AsNoTracking().Where(x => x.Enabled && x.IntegrationType == type);
        if (preferredId is not null)
            return await query.FirstOrDefaultAsync(x => x.Id == preferredId.Value, cancellationToken);
        return await query.OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private static bool RadarrMovieMatchesPath(JsonElement movie, string targetPath)
    {
        var normalizedTarget = NormalizePathForCompare(targetPath);
        foreach (var candidate in GetRadarrPathCandidates(movie).Select(NormalizePathForCompare))
        {
            if (string.Equals(candidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string?> GetRadarrPathCandidates(JsonElement movie)
    {
        if (movie.TryGetProperty("movieFile", out var movieFile) && movieFile.ValueKind == JsonValueKind.Object)
        {
            yield return IntegrationApiClient.TryGetString(movieFile, "path");
            var relativePath = IntegrationApiClient.TryGetString(movieFile, "relativePath");
            var folder = IntegrationApiClient.TryGetString(movie, "path");
            if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(relativePath))
                yield return Path.Combine(folder, relativePath);
        }
    }

    private static string? ExtractLanguage(JsonElement element)
    {
        if (element.TryGetProperty("originalLanguage", out var originalLanguage))
        {
            var normalized = ExtractLanguageValue(originalLanguage);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        if (element.TryGetProperty("language", out var languageElement))
        {
            var normalized = ExtractLanguageValue(languageElement);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        var language = IntegrationApiClient.TryGetString(element, "original_language");
        return LanguageNormalizer.Normalize(language);
    }

    private static string? ExtractLanguageValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            return int.TryParse(value, out var languageId)
                ? NormalizeServarrLanguageId(languageId)
                : LanguageNormalizer.Normalize(value);
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var id))
            return NormalizeServarrLanguageId(id);

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in new[] { "name", "nameLower", "title", "code", "iso2", "iso6391", "language" })
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            var normalized = ExtractLanguageValue(property);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        // Last resort: map known Servarr language IDs. Never return the raw number.
        if (element.TryGetProperty("id", out var idProperty) &&
            idProperty.ValueKind == JsonValueKind.Number &&
            idProperty.TryGetInt32(out var mappedLanguageId))
        {
            return NormalizeServarrLanguageId(mappedLanguageId);
        }

        return null;
    }

    private static string? ExtractSonarrLanguage(JsonElement series)
    {
        if (series.ValueKind != JsonValueKind.Object)
            return null;

        if (series.TryGetProperty("originalLanguage", out var originalLanguage))
        {
            var normalized = ExtractLanguageValue(originalLanguage);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        if (series.TryGetProperty("language", out var language))
        {
            var normalized = ExtractLanguageValue(language);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }

    private static string? NormalizeServarrLanguageId(int id)
    {
        return id switch
        {
            1 => "eng",
            2 => "fra",
            3 => "spa",
            4 => "deu",
            5 => "ita",
            6 => "dan",
            7 => "nld",
            8 => "jpn",
            9 => "isl",
            10 => "zho",
            11 => "rus",
            12 => "pol",
            13 => "vie",
            14 => "swe",
            15 => "nor",
            16 => "fin",
            17 => "tur",
            18 => "por",
            19 => "kor",
            _ => null
        };
    }

    private static Dictionary<string, string> ExtractRadarrIds(JsonElement movie)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddId(ids, "radarr", IntegrationApiClient.TryGetInt(movie, "id")?.ToString());
        AddId(ids, "tmdb", IntegrationApiClient.TryGetInt(movie, "tmdbId")?.ToString());
        AddId(ids, "imdb", IntegrationApiClient.TryGetString(movie, "imdbId"));
        return ids;
    }

    private static Dictionary<string, string> ExtractSonarrIds(JsonElement series)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddId(ids, "sonarr", IntegrationApiClient.TryGetInt(series, "id")?.ToString());
        AddId(ids, "tvdb", IntegrationApiClient.TryGetInt(series, "tvdbId")?.ToString());
        AddId(ids, "imdb", IntegrationApiClient.TryGetString(series, "imdbId"));
        return ids;
    }

    private static void AddId(Dictionary<string, string> ids, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) ids[key] = value;
    }

    private static string MapServerPathToIntegrationPath(string serverPath, IntegrationEntity integration)
    {
        var mappings = DeserializeMappings(integration.PathMappingsJson);
        var match = mappings
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerPath) && IsPathPrefix(serverPath, x.ServerPath))
            .OrderByDescending(x => x.ServerPath.Length)
            .FirstOrDefault();

        if (match is null)
            return serverPath;

        var suffix = serverPath[match.ServerPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
        return string.IsNullOrWhiteSpace(suffix) ? match.IntegrationPath : CombineIntegrationPath(match.IntegrationPath, suffix);
    }

    private static bool IsPathPrefix(string path, string prefix)
    {
        var nPath = NormalizePathForCompare(path);
        var nPrefix = NormalizePathForCompare(prefix);
        return string.Equals(nPath, nPrefix, StringComparison.OrdinalIgnoreCase) || nPath.StartsWith(nPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineIntegrationPath(string root, string suffix) => $"{root.TrimEnd('/').TrimEnd('\\')}/{suffix.Replace('\\', '/')}";

    private static string NormalizePathForCompare(string? path) => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static List<IntegrationPathMappingDto> DeserializeMappings(string json)
    {
        try { return JsonSerializer.Deserialize<List<IntegrationPathMappingDto>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto(); }
        catch { return new LibraryPolicyDto(); }
    }

    private static HashSet<string> ExtractAudioLanguages(string probeJson)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(probeJson);
        var root = doc.RootElement.TryGetProperty("probeJson", out var wrapped) ? wrapped : doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var stream in streams.EnumerateArray())
        {
            if (!string.Equals(IntegrationApiClient.TryGetString(stream, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!stream.TryGetProperty("tags", out var tags))
                continue;
            var language = LanguageNormalizer.Normalize(IntegrationApiClient.TryGetString(tags, "language"));
            if (!string.IsNullOrWhiteSpace(language))
                result.Add(language);
        }

        return result;
    }

    private sealed class MetadataMatch
    {
        public MetadataSource Source { get; set; }
        public string? OriginalLanguage { get; set; }
        public int? IntegrationId { get; set; }
        public string? IntegrationName { get; set; }
        public string MatchMethod { get; set; } = string.Empty;
        public string? MatchedPath { get; set; }
        public Dictionary<string, string> ExternalIds { get; set; } = [];
    }
}
