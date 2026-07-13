using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Server.Data;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class StorageMapImportService(
    TranscoderDbContext db,
    IOptions<StorageOptions> storageOptions,
    ILogger<StorageMapImportService> logger)
{
    public async Task<StorageMapImportResult> ImportAsync(string? manifestPath = null, CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(db, cancellationToken);

        var path = ResolveManifestPath(manifestPath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Storage map file was not found: {path}", path);

        var rows = await ReadManifestAsync(path, cancellationToken);
        var result = new StorageMapImportResult
        {
            ManifestPath = path,
            ManifestRows = rows.Count
        };

        var byRelative = rows
            .GroupBy(x => NormalizePath(x.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var byFileName = rows
            .GroupBy(x => Path.GetFileName(NormalizePath(x.RelativePath)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        var media = await db.MediaItems.AsNoTracking()
            .Select(x => new
            {
                x.Id,
                x.LibraryId,
                x.RelativePath,
                x.FileSizeBytes,
                x.LastModifiedUtc
            })
            .ToListAsync(cancellationToken);

        result.MediaConsidered = media.Count;
        var now = DateTime.UtcNow;

        foreach (var item in media)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizePath(item.RelativePath);
            StorageMapRow? match = null;

            if (!byRelative.TryGetValue(relative, out match))
            {
                var fileName = Path.GetFileName(relative);
                if (byFileName.TryGetValue(fileName, out var sameNameRows))
                {
                    var suffixMatches = sameNameRows
                        .Where(x => NormalizePath(x.RelativePath).EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (suffixMatches.Count == 1)
                        match = suffixMatches[0];
                    else if (suffixMatches.Count > 1)
                    {
                        result.AmbiguousMatches++;
                        continue;
                    }
                }
            }

            if (match is null)
            {
                result.UnmatchedMedia++;
                continue;
            }

            await UpsertMediaStorageAsync(
                item.Id,
                item.LibraryId,
                item.RelativePath,
                match.StorageKey,
                match.SizeBytes,
                match.ModifiedUnix,
                now,
                path,
                cancellationToken);

            result.MatchedMedia++;
        }

        result.ImportedUtc = now;
        logger.LogInformation("Imported Unraid storage map from {Path}. Rows={Rows}, matched={Matched}, unmatched={Unmatched}, ambiguous={Ambiguous}",
            path, result.ManifestRows, result.MatchedMedia, result.UnmatchedMedia, result.AmbiguousMatches);

        return result;
    }

    public async Task<StorageMapStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(db, cancellationToken);
        var status = new StorageMapStatus();
        var rows = await QueryAsync("select coalesce(SourceStorageKey, 'unknown') as StorageKey, count(*) as Count, sum(coalesce(SourceStorageSizeBytes,0)) as SizeBytes, sum(case when SourceStorageStale = 1 then 1 else 0 end) as StaleCount from MediaStorage group by coalesce(SourceStorageKey, 'unknown') order by StorageKey", cancellationToken);
        foreach (var row in rows)
        {
            status.StorageKeys.Add(new StorageMapStatusItem
            {
                StorageKey = Convert.ToString(row["StorageKey"], CultureInfo.InvariantCulture) ?? "unknown",
                Count = Convert.ToInt32(row["Count"], CultureInfo.InvariantCulture),
                SizeBytes = row["SizeBytes"] is DBNull ? 0 : Convert.ToInt64(row["SizeBytes"], CultureInfo.InvariantCulture),
                StaleCount = row["StaleCount"] is DBNull ? 0 : Convert.ToInt32(row["StaleCount"], CultureInfo.InvariantCulture)
            });
        }
        status.TotalMapped = status.StorageKeys.Sum(x => x.Count);
        status.TotalStale = status.StorageKeys.Sum(x => x.StaleCount);
        return status;
    }

    public static async Task EnsureTableAsync(TranscoderDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS MediaStorage (
    MediaItemId INTEGER NOT NULL PRIMARY KEY,
    LibraryId INTEGER NOT NULL,
    RelativePath TEXT NOT NULL,
    SourceStorageKey TEXT NULL,
    SourceStorageSizeBytes INTEGER NULL,
    SourceStorageModifiedUnix INTEGER NULL,
    SourceStorageImportedUtc TEXT NOT NULL,
    SourceStorageStale INTEGER NOT NULL DEFAULT 0,
    SourceStorageMapPath TEXT NULL
);
", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_MediaStorage_SourceStorageKey ON MediaStorage(SourceStorageKey);", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_MediaStorage_LibraryId_RelativePath ON MediaStorage(LibraryId, RelativePath);", cancellationToken);
    }

    public static async Task MarkMediaStorageStaleAsync(TranscoderDbContext db, long mediaItemId, CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(db, cancellationToken);
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE MediaStorage
SET SourceStorageStale = 1,
    SourceStorageKey = NULL,
    SourceStorageImportedUtc = $importedUtc
WHERE MediaItemId = $mediaItemId";
            AddParameter(command, "$mediaItemId", mediaItemId);
            AddParameter(command, "$importedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    public static string BuildFallbackStorageKey(string? relativePath, int depth = 2)
    {
        var normalized = NormalizePath(relativePath);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "unknown";
        return "folder:" + string.Join('/', parts.Take(Math.Max(1, depth)));
    }

    private string ResolveManifestPath(string? manifestPath)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath))
            return manifestPath;
        if (!string.IsNullOrWhiteSpace(storageOptions.Value.StorageMapPath))
            return storageOptions.Value.StorageMapPath!;
        return Path.Combine(storageOptions.Value.TranscoderRoot, "storage-map.tsv");
    }

    private static async Task<List<StorageMapRow>> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        var rows = new List<StorageMapRow>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            if (parts[0].Equals("relative_path", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = NormalizePath(parts[0]);
            var storageKey = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(storageKey))
                continue;

            long? sizeBytes = null;
            long? modifiedUnix = null;
            if (parts.Length > 2 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize))
                sizeBytes = parsedSize;
            if (parts.Length > 3 && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMtime))
                modifiedUnix = parsedMtime;

            rows.Add(new StorageMapRow(relativePath, storageKey, sizeBytes, modifiedUnix));
        }
        return rows;
    }

    private async Task UpsertMediaStorageAsync(
        long mediaItemId,
        int libraryId,
        string relativePath,
        string storageKey,
        long? sizeBytes,
        long? modifiedUnix,
        DateTime importedUtc,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO MediaStorage
    (MediaItemId, LibraryId, RelativePath, SourceStorageKey, SourceStorageSizeBytes, SourceStorageModifiedUnix, SourceStorageImportedUtc, SourceStorageStale, SourceStorageMapPath)
VALUES
    ($mediaItemId, $libraryId, $relativePath, $storageKey, $sizeBytes, $modifiedUnix, $importedUtc, 0, $manifestPath)
ON CONFLICT(MediaItemId) DO UPDATE SET
    LibraryId = excluded.LibraryId,
    RelativePath = excluded.RelativePath,
    SourceStorageKey = excluded.SourceStorageKey,
    SourceStorageSizeBytes = excluded.SourceStorageSizeBytes,
    SourceStorageModifiedUnix = excluded.SourceStorageModifiedUnix,
    SourceStorageImportedUtc = excluded.SourceStorageImportedUtc,
    SourceStorageStale = 0,
    SourceStorageMapPath = excluded.SourceStorageMapPath";

            AddParameter(command, "$mediaItemId", mediaItemId);
            AddParameter(command, "$libraryId", libraryId);
            AddParameter(command, "$relativePath", relativePath);
            AddParameter(command, "$storageKey", storageKey);
            AddParameter(command, "$sizeBytes", sizeBytes);
            AddParameter(command, "$modifiedUnix", modifiedUnix);
            AddParameter(command, "$importedUtc", importedUtc.ToString("O", CultureInfo.InvariantCulture));
            AddParameter(command, "$manifestPath", manifestPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizePath(string? value)
        => string.Join('/', (value ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private sealed record StorageMapRow(string RelativePath, string StorageKey, long? SizeBytes, long? ModifiedUnix);
}

public sealed class StorageMapImportResult
{
    public string ManifestPath { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
    public int ManifestRows { get; set; }
    public int MediaConsidered { get; set; }
    public int MatchedMedia { get; set; }
    public int UnmatchedMedia { get; set; }
    public int AmbiguousMatches { get; set; }
}

public sealed class StorageMapStatus
{
    public int TotalMapped { get; set; }
    public int TotalStale { get; set; }
    public List<StorageMapStatusItem> StorageKeys { get; set; } = [];
}

public sealed class StorageMapStatusItem
{
    public string StorageKey { get; set; } = string.Empty;
    public int Count { get; set; }
    public long SizeBytes { get; set; }
    public int StaleCount { get; set; }
}
