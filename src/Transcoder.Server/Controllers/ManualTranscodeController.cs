using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/manual-transcode")]
public sealed class ManualTranscodeController(TranscoderDbContext db, ILogger<ManualTranscodeController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpPost("start")]
    public async Task<ActionResult<ManualTranscodeSessionResultDto>> Start(ManualTranscodeStartRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId <= 0)
            return BadRequest(new { message = "LibraryId is required." });

        var path = NormalizeBrowserPath(request.Path);
        var toolName = string.IsNullOrWhiteSpace(request.ToolName) ? "Manual" : request.ToolName.Trim();

        await EnsureTablesAsync(cancellationToken);

        var libraryExists = await db.Libraries.AsNoTracking().AnyAsync(x => x.Id == request.LibraryId, cancellationToken);
        if (!libraryExists)
            return NotFound(new { message = "Library was not found." });

        var existingOpen = await GetOpenSessionAsync(request.LibraryId, path, cancellationToken);
        if (existingOpen is not null && !request.ReplaceOpenSession)
        {
            var existingSummary = await BuildSessionSummaryAsync(existingOpen.Id, cancellationToken);
            existingSummary.Message = "A manual transcode session is already open for this folder. Finish it, cancel it, or start again with ReplaceOpenSession=true.";
            return Conflict(existingSummary);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (existingOpen is not null && request.ReplaceOpenSession)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                update ManualTranscodeSessions
                set Status = 'Cancelled', FinishedUtc = {DateTime.UtcNow}, Notes = 'Replaced by a newer manual transcode baseline.'
                where Id = {existingOpen.Id};", cancellationToken);
        }

        var selected = (await db.MediaItems.AsNoTracking()
                .Where(x => x.LibraryId == request.LibraryId)
                .OrderBy(x => x.RelativePath)
                .ToListAsync(cancellationToken))
            .Where(x => IsUnderBrowserPath(x.RelativePath, path))
            .ToList();

        if (selected.Count == 0)
            return BadRequest(new { message = "No media files were found under this folder." });

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            insert into ManualTranscodeSessions (LibraryId, Path, ToolName, Status, CreatedUtc, Notes)
            values ({request.LibraryId}, {path}, {toolName}, 'Open', {now}, {request.Notes});", cancellationToken);

        var sessionId = await ExecuteScalarLongAsync("select last_insert_rowid();", cancellationToken);

        foreach (var item in selected)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                insert into ManualTranscodeSessionItems
                (SessionId, MediaItemId, RelativePath, FullPath, BeforeSizeBytes, Status)
                values ({sessionId}, {item.Id}, {item.RelativePath}, {item.FullPath}, {item.FileSizeBytes}, 'Captured');", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var result = await BuildSessionSummaryAsync(sessionId, cancellationToken);
        result.Message = $"Manual transcode baseline captured for {selected.Count} file(s). Replace files externally, then click Finish Manual Transcode.";
        return Ok(result);
    }

    [HttpPost("finish")]
    public async Task<ActionResult<ManualTranscodeSessionResultDto>> Finish(ManualTranscodeFinishRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId <= 0)
            return BadRequest(new { message = "LibraryId is required." });

        var path = NormalizeBrowserPath(request.Path);
        var toolNameOverride = string.IsNullOrWhiteSpace(request.ToolName) ? null : request.ToolName.Trim();

        await EnsureTablesAsync(cancellationToken);

        var session = request.SessionId is > 0
            ? await GetSessionAsync(request.SessionId.Value, cancellationToken)
            : await GetOpenSessionAsync(request.LibraryId, path, cancellationToken);

        if (session is null)
            return NotFound(new { message = "No open manual transcode session was found for this folder." });

        if (session.LibraryId != request.LibraryId)
            return BadRequest(new { message = "The manual transcode session belongs to a different library." });

        var effectiveToolName = toolNameOverride ?? session.ToolName;
        var items = await GetSessionItemsAsync(session.Id, cancellationToken);
        if (items.Count == 0)
            return BadRequest(new { message = "The manual transcode session has no captured files." });

        var mediaIds = items.Select(x => x.MediaItemId).ToHashSet();
        var mediaById = await db.MediaItems
            .Where(x => mediaIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var now = DateTime.UtcNow;
        var changed = 0;
        var unchanged = 0;
        var missing = 0;
        var grew = 0;
        var jobsInserted = 0;
        var probesQueued = 0;
        long beforeTotal = 0;
        long afterTotal = 0;
        long savedTotal = 0;
        var messages = new List<string>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var sessionItem in items)
        {
            beforeTotal += sessionItem.BeforeSizeBytes;

            if (!mediaById.TryGetValue(sessionItem.MediaItemId, out var media))
            {
                missing++;
                await UpdateSessionItemAsync(sessionItem.Id, null, null, "MissingMedia", "Media item no longer exists.", cancellationToken);
                continue;
            }

            var pathToCheck = !string.IsNullOrWhiteSpace(media.FullPath) ? media.FullPath : sessionItem.FullPath;
            if (!System.IO.File.Exists(pathToCheck))
            {
                missing++;
                await UpdateSessionItemAsync(sessionItem.Id, null, null, "MissingFile", $"File not found: {pathToCheck}", cancellationToken);
                if (messages.Count < 12)
                    messages.Add($"Missing: {media.RelativePath}");
                continue;
            }

            var fileInfo = new FileInfo(pathToCheck);
            var afterSize = fileInfo.Length;
            var saved = Math.Max(0, sessionItem.BeforeSizeBytes - afterSize);
            afterTotal += afterSize;
            savedTotal += saved;

            media.FileSizeBytes = afterSize;
            media.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
            media.UpdatedUtc = now;

            if (afterSize < sessionItem.BeforeSizeBytes)
            {
                changed++;
                ClearStaleProbePlan(media);
                await UpdateSessionItemAsync(sessionItem.Id, afterSize, saved, "Changed", null, cancellationToken);
                ApplyExternalTranscodeStats(media, sessionItem.BeforeSizeBytes, afterSize, saved, effectiveToolName, session.Id, now);

                var alreadyInserted = await db.Jobs.AnyAsync(j =>
                    j.MediaItemId == media.Id &&
                    j.JobType == JobType.Transcode &&
                    j.Status == JobStatus.Completed &&
                    j.LastMessage != null &&
                    j.LastMessage.Contains($"manual session {session.Id}"), cancellationToken);

                if (!alreadyInserted)
                {
                    db.Jobs.Add(BuildExternalTranscodeJob(media, sessionItem.BeforeSizeBytes, afterSize, saved, effectiveToolName, session.Id, now));
                    jobsInserted++;
                }

                if (request.QueueFreshProbe)
                {
                    db.Jobs.Add(BuildProbeJob(media, now));
                    media.Status = MediaStatus.ProbeQueued;
                    probesQueued++;
                }
                else
                {
                    media.Status = MediaStatus.ReplacedTranscoded;
                }
            }
            else if (afterSize == sessionItem.BeforeSizeBytes)
            {
                unchanged++;
                await UpdateSessionItemAsync(sessionItem.Id, afterSize, 0, "Unchanged", null, cancellationToken);
                if (request.QueueFreshProbe)
                {
                    ClearStaleProbePlan(media);
                    media.Status = MediaStatus.ProbeQueued;
                    db.Jobs.Add(BuildProbeJob(media, now));
                    probesQueued++;
                }
            }
            else
            {
                grew++;
                await UpdateSessionItemAsync(sessionItem.Id, afterSize, 0, "Grew", "Current file is larger than the captured baseline; no saving recorded.", cancellationToken);
                ClearStaleProbePlan(media);
                if (request.QueueFreshProbe)
                {
                    media.Status = MediaStatus.ProbeQueued;
                    db.Jobs.Add(BuildProbeJob(media, now));
                    probesQueued++;
                }
            }
        }

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            update ManualTranscodeSessions
            set Status = 'Finished', FinishedUtc = {now}, ToolName = {effectiveToolName}, Notes = {request.Notes}
            where Id = {session.Id};", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = await BuildSessionSummaryAsync(session.Id, cancellationToken);
        result.ChangedFiles = changed;
        result.UnchangedFiles = unchanged;
        result.MissingFiles = missing;
        result.GrewFiles = grew;
        result.JobsInserted = jobsInserted;
        result.ProbesQueued = probesQueued;
        result.BeforeSizeBytes = beforeTotal;
        result.AfterSizeBytes = afterTotal;
        result.SavedBytes = savedTotal;
        result.Messages = messages;
        result.Message = $"Manual transcode finished. Changed: {changed}, unchanged: {unchanged}, missing: {missing}, grew: {grew}. Saved {FormatBytes(savedTotal)}. Completed jobs inserted: {jobsInserted}. Fresh probes queued: {probesQueued}.";
        return Ok(result);
    }

    [HttpPost("cancel")]
    public async Task<ActionResult<ManualTranscodeSessionResultDto>> Cancel(ManualTranscodeCancelRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId <= 0 && request.SessionId is null)
            return BadRequest(new { message = "SessionId or LibraryId is required." });

        await EnsureTablesAsync(cancellationToken);
        var path = NormalizeBrowserPath(request.Path);
        var session = request.SessionId is > 0
            ? await GetSessionAsync(request.SessionId.Value, cancellationToken)
            : await GetOpenSessionAsync(request.LibraryId, path, cancellationToken);

        if (session is null)
            return NotFound(new { message = "No open manual transcode session was found." });

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            update ManualTranscodeSessions
            set Status = 'Cancelled', FinishedUtc = {DateTime.UtcNow}, Notes = {request.Notes}
            where Id = {session.Id};", cancellationToken);

        var result = await BuildSessionSummaryAsync(session.Id, cancellationToken);
        result.Message = "Manual transcode session cancelled. No media files or jobs were changed.";
        return Ok(result);
    }

    [HttpGet("status")]
    public async Task<ActionResult<ManualTranscodeSessionResultDto>> Status([FromQuery] int libraryId, [FromQuery] string? path = null, CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
            return BadRequest(new { message = "LibraryId is required." });

        await EnsureTablesAsync(cancellationToken);
        var currentPath = NormalizeBrowserPath(path);
        var session = await GetOpenSessionAsync(libraryId, currentPath, cancellationToken)
            ?? await GetLatestSessionAsync(libraryId, currentPath, cancellationToken);

        if (session is null)
            return NotFound(new { message = "No manual transcode session was found for this folder." });

        return Ok(await BuildSessionSummaryAsync(session.Id, cancellationToken));
    }


    private static void ClearStaleProbePlan(MediaItemEntity media)
    {
        media.StagingPath = null;
        media.ProbeJson = null;
        media.PlanJson = null;
        media.PlanHash = null;
        media.PlanReviewJson = null;
        media.PlanCreatedUtc = null;
        media.PlanReviewedUtc = null;
    }

    private static void ApplyExternalTranscodeStats(MediaItemEntity media, long beforeSizeBytes, long afterSizeBytes, long savedBytes, string toolName, long sessionId, DateTime completedUtc)
    {
        var existing = MediaProcessingStatsStore.Read(media);
        var originalSize = existing.OriginalSizeBytes ?? beforeSizeBytes;
        var totalSaved = Math.Max(0, originalSize - afterSizeBytes);

        existing.History ??= [];
        var stats = new MediaProcessingStats
        {
            OriginalSizeBytes = originalSize,
            OutputSizeBytes = afterSizeBytes,
            SavedBytes = savedBytes,
            CleanupSavedBytes = existing.CleanupSavedBytes,
            TranscodeSavedBytes = savedBytes,
            TotalSavedBytes = totalSaved,
            LastCompletedWorkType = JobType.Transcode,
            CompletedUtc = completedUtc,
            StagingTransferComplete = true,
            StagingOutputPath = media.FullPath,
            StagingCompleteMarkerPath = existing.StagingCompleteMarkerPath,
            ElapsedSeconds = existing.ElapsedSeconds,
            ReplacedOriginal = true,
            ReplacementBackupPath = existing.ReplacementBackupPath,
            ReplacedUtc = completedUtc,
            History = existing.History
        };

        MediaProcessingStatsStore.AddHistory(stats, new ProcessingHistoryEntry
        {
            Stage = "ExternalTranscodeReplacement",
            JobType = JobType.Transcode,
            CompletedUtc = completedUtc,
            BeforeSizeBytes = beforeSizeBytes,
            AfterSizeBytes = afterSizeBytes,
            SavedBytes = savedBytes,
            TotalSavedBytes = totalSaved,
            InputPath = media.FullPath,
            OutputPath = media.FullPath,
            Message = $"External transcode imported from {toolName} manual session {sessionId}."
        });

        MediaProcessingStatsStore.Write(media, stats);
    }

    private static JobEntity BuildExternalTranscodeJob(MediaItemEntity media, long beforeSizeBytes, long afterSizeBytes, long savedBytes, string toolName, long sessionId, DateTime completedUtc)
    {
        var payload = JsonSerializer.Serialize(new
        {
            inputPath = media.FullPath,
            externalTool = toolName,
            manualTranscodeSessionId = sessionId,
            importedExternalCompletion = true
        }, JsonOptions);

        var result = JsonSerializer.Serialize(new
        {
            inputPath = media.FullPath,
            stagingOutputPath = (string?)null,
            externalTool = toolName,
            manualTranscodeSessionId = sessionId,
            externalCompletion = true,
            inputFileSizeBytes = beforeSizeBytes,
            outputFileSizeBytes = afterSizeBytes,
            savedBytes,
            completedUtc,
            jobType = JobType.Transcode.ToString(),
            operation = "external-transcode"
        }, JsonOptions);

        return new JobEntity
        {
            JobType = JobType.Transcode,
            Status = JobStatus.Completed,
            LibraryId = media.LibraryId,
            MediaItemId = media.Id,
            PayloadJson = payload,
            ResultJson = result,
            LeasedByWorkerId = $"external-{toolName.ToLowerInvariant()}",
            LeasedByWorkerInstanceId = "manual-import",
            CreatedUtc = completedUtc,
            QueuedUtc = completedUtc,
            LeaseStartedUtc = completedUtc,
            LeaseLastSeenUtc = completedUtc,
            LeaseExpiresUtc = completedUtc,
            StartedUtc = completedUtc,
            CompletedUtc = completedUtc,
            AttemptNumber = 1,
            MaxAttempts = 1,
            Progress = 100,
            LastMessage = $"External {toolName} transcode imported as completed from manual session {sessionId}."
        };
    }

    private static JobEntity BuildProbeJob(MediaItemEntity media, DateTime queuedUtc)
    {
        return new JobEntity
        {
            JobType = JobType.Probe,
            Status = JobStatus.Queued,
            LibraryId = media.LibraryId,
            MediaItemId = media.Id,
            CreatedUtc = queuedUtc,
            QueuedUtc = queuedUtc,
            PayloadJson = JsonSerializer.Serialize(new
            {
                libraryId = media.LibraryId,
                mediaId = media.Id,
                inputPath = media.FullPath,
                fileSizeBytes = media.FileSizeBytes,
                lastModifiedUtc = media.LastModifiedUtc
            }, JsonOptions),
            LastMessage = "Queued after manual external transcode import."
        };
    }

    private async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            create table if not exists ManualTranscodeSessions (
                Id integer primary key autoincrement,
                LibraryId integer not null,
                Path text not null,
                ToolName text not null,
                Status text not null,
                CreatedUtc text not null,
                FinishedUtc text null,
                Notes text null
            );", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(@"
            create table if not exists ManualTranscodeSessionItems (
                Id integer primary key autoincrement,
                SessionId integer not null,
                MediaItemId integer not null,
                RelativePath text not null,
                FullPath text not null,
                BeforeSizeBytes integer not null,
                AfterSizeBytes integer null,
                SavedBytes integer null,
                Status text not null,
                Error text null
            );", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(@"
            create index if not exists IX_ManualTranscodeSessions_Library_Path_Status
            on ManualTranscodeSessions (LibraryId, Path, Status);", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(@"
            create index if not exists IX_ManualTranscodeSessionItems_Session
            on ManualTranscodeSessionItems (SessionId);", cancellationToken);
    }

    private async Task<ManualTranscodeSessionRow?> GetOpenSessionAsync(int libraryId, string path, CancellationToken cancellationToken)
    {
        return await ReadSessionAsync(@"
            select Id, LibraryId, Path, ToolName, Status, CreatedUtc, FinishedUtc, Notes
            from ManualTranscodeSessions
            where LibraryId = @libraryId and Path = @path and Status = 'Open'
            order by Id desc
            limit 1;", command =>
        {
            AddParameter(command, "@libraryId", libraryId);
            AddParameter(command, "@path", path);
        }, cancellationToken);
    }

    private async Task<ManualTranscodeSessionRow?> GetLatestSessionAsync(int libraryId, string path, CancellationToken cancellationToken)
    {
        return await ReadSessionAsync(@"
            select Id, LibraryId, Path, ToolName, Status, CreatedUtc, FinishedUtc, Notes
            from ManualTranscodeSessions
            where LibraryId = @libraryId and Path = @path
            order by Id desc
            limit 1;", command =>
        {
            AddParameter(command, "@libraryId", libraryId);
            AddParameter(command, "@path", path);
        }, cancellationToken);
    }

    private async Task<ManualTranscodeSessionRow?> GetSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        return await ReadSessionAsync(@"
            select Id, LibraryId, Path, ToolName, Status, CreatedUtc, FinishedUtc, Notes
            from ManualTranscodeSessions
            where Id = @sessionId;", command => AddParameter(command, "@sessionId", sessionId), cancellationToken);
    }

    private async Task<ManualTranscodeSessionRow?> ReadSessionAsync(string sql, Action<DbCommand> addParameters, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is not null)
            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        addParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ManualTranscodeSessionRow(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ReadNullableDateTime(reader, 5),
            ReadNullableDateTime(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private async Task<List<ManualTranscodeItemRow>> GetSessionItemsAsync(long sessionId, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            select Id, MediaItemId, RelativePath, FullPath, BeforeSizeBytes
            from ManualTranscodeSessionItems
            where SessionId = @sessionId
            order by RelativePath;";
        if (db.Database.CurrentTransaction is not null)
            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        AddParameter(command, "@sessionId", sessionId);

        var rows = new List<ManualTranscodeItemRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ManualTranscodeItemRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    private async Task<ManualTranscodeSessionResultDto> BuildSessionSummaryAsync(long sessionId, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken) ?? throw new InvalidOperationException($"Manual transcode session {sessionId} was not found.");
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            select
              count(*) as ItemCount,
              coalesce(sum(BeforeSizeBytes), 0) as BeforeBytes,
              coalesce(sum(coalesce(AfterSizeBytes, 0)), 0) as AfterBytes,
              coalesce(sum(coalesce(SavedBytes, 0)), 0) as SavedBytes,
              coalesce(sum(case when Status = 'Changed' then 1 else 0 end), 0) as ChangedFiles,
              coalesce(sum(case when Status = 'Unchanged' then 1 else 0 end), 0) as UnchangedFiles,
              coalesce(sum(case when Status = 'MissingFile' or Status = 'MissingMedia' then 1 else 0 end), 0) as MissingFiles,
              coalesce(sum(case when Status = 'Grew' then 1 else 0 end), 0) as GrewFiles
            from ManualTranscodeSessionItems
            where SessionId = @sessionId;";
        if (db.Database.CurrentTransaction is not null)
            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        AddParameter(command, "@sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new ManualTranscodeSessionResultDto
        {
            SessionId = session.Id,
            LibraryId = session.LibraryId,
            Path = session.Path,
            ToolName = session.ToolName,
            Status = session.Status,
            CreatedUtc = session.CreatedUtc,
            FinishedUtc = session.FinishedUtc,
            Notes = session.Notes,
            ItemCount = Convert.ToInt32(reader.GetValue(0)),
            BeforeSizeBytes = Convert.ToInt64(reader.GetValue(1)),
            AfterSizeBytes = Convert.ToInt64(reader.GetValue(2)),
            SavedBytes = Convert.ToInt64(reader.GetValue(3)),
            ChangedFiles = Convert.ToInt32(reader.GetValue(4)),
            UnchangedFiles = Convert.ToInt32(reader.GetValue(5)),
            MissingFiles = Convert.ToInt32(reader.GetValue(6)),
            GrewFiles = Convert.ToInt32(reader.GetValue(7))
        };
    }

    private async Task UpdateSessionItemAsync(long id, long? afterSizeBytes, long? savedBytes, string status, string? error, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            update ManualTranscodeSessionItems
            set AfterSizeBytes = {afterSizeBytes}, SavedBytes = {savedBytes}, Status = {status}, Error = {error}
            where Id = {id};", cancellationToken);
    }

    private async Task<long> ExecuteScalarLongAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is not null)
            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }

    private static DateTime? ReadNullableDateTime(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        if (value is DateTime dt)
            return dt;

        return DateTime.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeBrowserPath(string? value)
        => string.Join('/', (value ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsUnderBrowserPath(string relativePath, string currentPath)
    {
        var normalized = NormalizeBrowserPath(relativePath);
        if (string.IsNullOrWhiteSpace(currentPath))
            return true;

        return normalized.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(currentPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private sealed record ManualTranscodeSessionRow(long Id, int LibraryId, string Path, string ToolName, string Status, DateTime? CreatedUtc, DateTime? FinishedUtc, string? Notes);
    private sealed record ManualTranscodeItemRow(long Id, long MediaItemId, string RelativePath, string FullPath, long BeforeSizeBytes);
}

public sealed class ManualTranscodeStartRequest
{
    public int LibraryId { get; set; }
    public string? Path { get; set; }
    public string? ToolName { get; set; } = "HandBrake";
    public string? Notes { get; set; }
    public bool ReplaceOpenSession { get; set; }
}

public sealed class ManualTranscodeFinishRequest
{
    public long? SessionId { get; set; }
    public int LibraryId { get; set; }
    public string? Path { get; set; }
    public string? ToolName { get; set; }
    public string? Notes { get; set; }
    public bool QueueFreshProbe { get; set; } = true;
}

public sealed class ManualTranscodeCancelRequest
{
    public long? SessionId { get; set; }
    public int LibraryId { get; set; }
    public string? Path { get; set; }
    public string? Notes { get; set; }
}

public sealed class ManualTranscodeSessionResultDto
{
    public long SessionId { get; set; }
    public int LibraryId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CreatedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? Notes { get; set; }
    public int ItemCount { get; set; }
    public int ChangedFiles { get; set; }
    public int UnchangedFiles { get; set; }
    public int MissingFiles { get; set; }
    public int GrewFiles { get; set; }
    public int JobsInserted { get; set; }
    public int ProbesQueued { get; set; }
    public long BeforeSizeBytes { get; set; }
    public long AfterSizeBytes { get; set; }
    public long SavedBytes { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = [];
}
