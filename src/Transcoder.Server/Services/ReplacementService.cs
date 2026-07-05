using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class ReplacementService(
    TranscoderDbContext db,
    IOptions<StorageOptions> storageOptions,
    ILogger<ReplacementService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ReplaceMediaResultDto> ReplaceMediaAsync(long mediaId, CancellationToken cancellationToken = default)
    {
        var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media?.Library is null)
            return Reject(mediaId, "Media item was not found.");

        var policy = DeserializePolicy(media.Library.PolicyJson);
        if (!policy.Output.ReplaceOriginals)
            return Reject(media.Id, "This library does not allow replacing originals. Enable Output > Replace originals first.", media.Status);

        if (media.Status is not (MediaStatus.StagedCleaned or MediaStatus.Staged or MediaStatus.Approved))
            return Reject(media.Id, $"Media is {media.Status}; only staged/approved media can replace originals.", media.Status);

        var stats = MediaProcessingStatsStore.Read(media);
        var stagingPath = !string.IsNullOrWhiteSpace(media.StagingPath) ? media.StagingPath : stats.StagingOutputPath;
        if (string.IsNullOrWhiteSpace(stagingPath))
            return Reject(media.Id, "No staged output path is recorded.", media.Status);

        if (!stats.StagingTransferComplete)
            return Reject(media.Id, "Staging transfer is not marked complete. Refusing to replace original.", media.Status);

        var markerPath = stats.StagingCompleteMarkerPath ?? stagingPath + ".complete.json";
        if (!File.Exists(markerPath))
            return Reject(media.Id, $"Staging completion marker was not found: {markerPath}", media.Status);

        if (!File.Exists(stagingPath))
            return Reject(media.Id, $"Staged output does not exist: {stagingPath}", media.Status);

        if (!File.Exists(media.FullPath))
            return Reject(media.Id, $"Original file does not exist: {media.FullPath}", media.Status);

        var originalInfo = new FileInfo(media.FullPath);
        var stagedInfo = new FileInfo(stagingPath);
        if (originalInfo.Length != media.FileSizeBytes)
            return Reject(media.Id, $"Original file size changed since planning. Expected {media.FileSizeBytes} bytes, found {originalInfo.Length}. Re-scan/re-probe before replacing.", media.Status);

        if (Math.Abs((originalInfo.LastWriteTimeUtc - media.LastModifiedUtc).TotalSeconds) > 5)
            return Reject(media.Id, "Original modified time changed since planning. Re-scan/re-probe before replacing.", media.Status);

        var backupPath = BuildBackupPath(media.Library, media);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        var originalPath = media.FullPath;
        var wasCleanup = media.Status == MediaStatus.StagedCleaned;
        try
        {
            MoveFile(originalPath, backupPath, overwrite: false);
            try
            {
                MoveFile(stagingPath, originalPath, overwrite: false);
            }
            catch
            {
                // Best-effort rollback so a failed replacement does not leave the library path empty.
                if (!File.Exists(originalPath) && File.Exists(backupPath))
                    MoveFile(backupPath, originalPath, overwrite: false);
                throw;
            }

            TryDelete(markerPath);
            TryDeleteEmptyDirectories(Path.GetDirectoryName(stagingPath), storageOptions.Value.StagingRoot);

            var replacementInfo = new FileInfo(originalPath);
            var firstOriginalSize = stats.OriginalSizeBytes ?? originalInfo.Length;
            var latestSaved = Math.Max(0, firstOriginalSize - replacementInfo.Length);
            stats.OutputSizeBytes = replacementInfo.Length;
            stats.TotalSavedBytes = latestSaved;
            stats.ReplacedOriginal = true;
            stats.ReplacementBackupPath = backupPath;
            stats.ReplacedUtc = DateTime.UtcNow;
            MediaProcessingStatsStore.AddHistory(stats, new ProcessingHistoryEntry
            {
                Stage = wasCleanup ? "ReplaceCleanedOriginal" : "ReplaceTranscodedOriginal",
                JobType = JobType.ReplaceOriginal,
                CompletedUtc = DateTime.UtcNow,
                BeforeSizeBytes = originalInfo.Length,
                AfterSizeBytes = replacementInfo.Length,
                SavedBytes = Math.Max(0, originalInfo.Length - replacementInfo.Length),
                TotalSavedBytes = latestSaved,
                InputPath = stagingPath,
                OutputPath = originalPath,
                BackupPath = backupPath,
                Message = "Staged output replaced original; original moved to quarantine."
            });
            MediaProcessingStatsStore.Write(media, stats);

            media.FileSizeBytes = replacementInfo.Length;
            media.LastModifiedUtc = replacementInfo.LastWriteTimeUtc;
            media.StagingPath = null;
            media.ProbeJson = null;
            media.PlanJson = null;
            media.PlanHash = null;
            media.PlanReviewJson = null;
            media.PlanCreatedUtc = null;
            media.PlanReviewedUtc = null;
            media.Status = wasCleanup ? MediaStatus.ReplacedCleaned : MediaStatus.ReplacedTranscoded;
            media.UpdatedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
            return new ReplaceMediaResultDto
            {
                MediaId = media.Id,
                Accepted = true,
                Replaced = true,
                Message = "Original replaced successfully. The old file is in quarantine and the media item needs a fresh probe before further work.",
                MediaStatus = media.Status,
                OriginalPath = originalPath,
                StagingPath = stagingPath,
                BackupPath = backupPath,
                OriginalSizeBytes = firstOriginalSize,
                ReplacementSizeBytes = replacementInfo.Length,
                TotalSavedBytes = latestSaved
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to replace original for media {MediaId}", media.Id);
            media.Status = MediaStatus.ReplaceFailed;
            media.UpdatedUtc = DateTime.UtcNow;
            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = media.Id,
                ReviewType = ReviewType.OutputValidationWarning,
                Severity = ReviewSeverity.Blocking,
                Reason = "Replace original failed.",
                DetailsJson = JsonSerializer.Serialize(new { error = ex.Message, stagingPath, originalPath, backupPath }, JsonOptions)
            });
            await db.SaveChangesAsync(cancellationToken);
            return Reject(media.Id, $"Replace failed: {ex.Message}", media.Status);
        }
    }

    public async Task<ReplaceLibraryResultDto> ReplaceLibraryAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var ids = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId && (x.Status == MediaStatus.StagedCleaned || x.Status == MediaStatus.Staged || x.Status == MediaStatus.Approved))
            .OrderBy(x => x.RelativePath)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var result = new ReplaceLibraryResultDto { LibraryId = libraryId, Considered = ids.Count };
        foreach (var id in ids)
        {
            var item = await ReplaceMediaAsync(id, cancellationToken);
            if (item.Replaced) result.Replaced++;
            else result.Skipped++;
            if (!item.Replaced && result.Messages.Count < 10)
                result.Messages.Add($"Media {id}: {item.Message}");
        }
        if (result.Messages.Count == 0)
            result.Messages.Add($"Replaced {result.Replaced} item(s). {result.Skipped} skipped.");
        return result;
    }

    private string BuildBackupPath(LibraryEntity library, MediaItemEntity media)
    {
        var libraryName = string.Join("_", library.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(libraryName)) libraryName = $"library-{library.Id}";
        var relative = media.RelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(relative);
        var extension = Path.GetExtension(relative);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        return Path.Combine(storageOptions.Value.TranscoderRoot, "originals", libraryName, directory, $"{name}.original-{stamp}{extension}");
    }

    private static void MoveFile(string source, string destination, bool overwrite)
    {
        if (File.Exists(destination))
        {
            if (!overwrite) throw new IOException($"Destination already exists: {destination}");
            File.Delete(destination);
        }
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteEmptyDirectories(string? start, string stopAtRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(start)) return;
            var root = Path.GetFullPath(stopAtRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrWhiteSpace(current) && current.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
                Directory.Delete(current);
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }
        catch { }
    }

    private static ReplaceMediaResultDto Reject(long mediaId, string message, MediaStatus? status = null) => new()
    {
        MediaId = mediaId,
        Accepted = false,
        Replaced = false,
        MediaStatus = status,
        Message = message
    };

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto(); }
        catch { return new LibraryPolicyDto(); }
    }
}
