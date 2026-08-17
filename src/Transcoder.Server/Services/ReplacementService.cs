using System.Diagnostics;
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
    ServerActivityState activity,
    ILogger<ReplacementService> logger)
{
    private const int CopyBufferSize = 4 * 1024 * 1024;
    private static readonly TimeSpan ActivityUpdateInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ReplaceMediaResultDto> ReplaceMediaAsync(long mediaId, CancellationToken cancellationToken = default)
    {
        await activity.ReplacementGate.WaitAsync(cancellationToken);
        var activityStarted = false;

        try
        {
            var media = await db.MediaItems.Include(x => x.Library).FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
            if (media?.Library is null)
                return Reject(mediaId, "Media item was not found.");

            var policy = DeserializePolicy(media.Library.PolicyJson);
            if (!policy.Output.ReplaceOriginals)
                return Reject(media.Id, "This library does not allow replacing originals. Enable Output > Replace originals first.", media.Status);

            if (media.Status is not (MediaStatus.StagedCleaned or MediaStatus.Staged or MediaStatus.Approved or MediaStatus.ReplaceFailed))
                return Reject(media.Id, $"Media is {media.Status}; only staged/approved or replace-failed media can replace originals.", media.Status);

            var stats = MediaProcessingStatsStore.Read(media);
            var stagingPath = !string.IsNullOrWhiteSpace(media.StagingPath) ? media.StagingPath : stats.StagingOutputPath;

            activity.Start(new ServerActivityOperation
            {
                Operation = "ReplaceOriginal",
                MediaId = media.Id,
                LibraryId = media.LibraryId,
                LibraryName = media.Library.Name,
                RelativePath = media.RelativePath,
                OriginalPath = media.FullPath,
                StagingPath = stagingPath,
                Stage = "Checking replacement safety",
                Message = "Validating staging marker, source file and current library file before replacement."
            });
            activityStarted = true;

            if (string.IsNullOrWhiteSpace(stagingPath))
                return FinishRejected(media.Id, "No staged output path is recorded.", media.Status);

            if (!stats.StagingTransferComplete)
                return FinishRejected(media.Id, "Staging transfer is not marked complete. Refusing to replace original.", media.Status);

            var markerPath = stats.StagingCompleteMarkerPath ?? stagingPath + ".complete.json";
            if (!File.Exists(markerPath))
                return FinishRejected(media.Id, $"Staging completion marker was not found: {markerPath}", media.Status);

            if (!File.Exists(stagingPath))
                return FinishRejected(media.Id, $"Staged output does not exist: {stagingPath}", media.Status);

            if (!File.Exists(media.FullPath))
                return FinishRejected(media.Id, $"Original file does not exist: {media.FullPath}", media.Status);

            var originalInfo = new FileInfo(media.FullPath);
            var stagedInfo = new FileInfo(stagingPath);

            if (originalInfo.Length != media.FileSizeBytes)
                return FinishRejected(media.Id,
                    $"Original file size changed since planning. Expected {media.FileSizeBytes} bytes, found {originalInfo.Length}. Re-scan/re-probe before replacing.",
                    media.Status);

            if (Math.Abs((originalInfo.LastWriteTimeUtc - media.LastModifiedUtc).TotalSeconds) > 5)
                return FinishRejected(media.Id,
                    "Original modified time changed since planning. Re-scan/re-probe before replacing.",
                    media.Status);

            var wasCleanup = media.Status == MediaStatus.StagedCleaned || stats.LastCompletedWorkType == JobType.Cleanup;

            if (stagedInfo.Length >= originalInfo.Length)
            {
                var differenceBytes = stagedInfo.Length - originalInfo.Length;
                var attemptedWorkType = stats.LastCompletedWorkType ?? (wasCleanup ? JobType.Cleanup : JobType.Transcode);

                logger.LogWarning(
                    "Replacement skipped because {WorkType} output is not smaller: MediaId={MediaId}; OriginalBytes={OriginalBytes}; StagedBytes={StagedBytes}; DifferenceBytes={DifferenceBytes}; Original retained",
                    attemptedWorkType, media.Id, originalInfo.Length, stagedInfo.Length, differenceBytes);

                MediaProcessingStatsStore.AddHistory(stats, new ProcessingHistoryEntry
                {
                    Stage = wasCleanup ? "CleanupNotBeneficial" : "TranscodeNotBeneficial",
                    JobType = attemptedWorkType,
                    CompletedUtc = DateTime.UtcNow,
                    BeforeSizeBytes = originalInfo.Length,
                    AfterSizeBytes = stagedInfo.Length,
                    SavedBytes = 0,
                    TotalSavedBytes = stats.TotalSavedBytes,
                    InputPath = media.FullPath,
                    OutputPath = stagingPath,
                    Message = $"Staged output was {differenceBytes} byte(s) larger than or equal to the current library file. Replacement was skipped and the original was retained."
                });

                stats.StagingTransferComplete = false;
                stats.StagingOutputPath = null;
                stats.StagingCompleteMarkerPath = null;
                MediaProcessingStatsStore.Write(media, stats);

                TryDelete(stagingPath);
                TryDelete(markerPath);
                TryDeleteEmptyDirectories(Path.GetDirectoryName(stagingPath), storageOptions.Value.StagingRoot);

                media.StagingPath = null;
                media.Status = MediaStatus.Skipped;
                media.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                var message = $"Replacement skipped: staged {attemptedWorkType} output is not smaller than the current file ({stagedInfo.Length} >= {originalInfo.Length} bytes). Original retained.";
                activity.Complete(false, message);

                return new ReplaceMediaResultDto
                {
                    MediaId = media.Id,
                    Accepted = true,
                    Replaced = false,
                    Message = message,
                    MediaStatus = media.Status,
                    OriginalPath = media.FullPath,
                    StagingPath = stagingPath,
                    OriginalSizeBytes = stats.OriginalSizeBytes ?? originalInfo.Length,
                    ReplacementSizeBytes = stagedInfo.Length,
                    TotalSavedBytes = stats.TotalSavedBytes
                };
            }

            var keepOriginalsQuarantine = storageOptions.Value.KeepOriginalsQuarantine;
            var backupPath = keepOriginalsQuarantine
                ? BuildBackupPath(media.Library, media)
                : BuildTemporaryRollbackPath(media);

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

            logger.LogInformation(
                "Server replacement started: MediaId={MediaId}; Library={Library}; Media={Media}; Original={Original}; Staging={Staging}; OriginalBytes={OriginalBytes}; StagedBytes={StagedBytes}; KeepQuarantine={KeepQuarantine}; Backup={Backup}",
                media.Id, media.Library.Name, media.RelativePath, media.FullPath, stagingPath,
                originalInfo.Length, stagedInfo.Length, keepOriginalsQuarantine, backupPath);

            var originalPath = media.FullPath;

            try
            {
                activity.Update(
                    "Protecting original",
                    keepOriginalsQuarantine
                        ? "Moving/copying the current library file into originals quarantine."
                        : "Moving the current library file to a temporary rollback path.",
                    0,
                    originalInfo.Length);

                await MoveFileSafelyAsync(
                    originalPath,
                    backupPath,
                    "Protecting original",
                    keepOriginalsQuarantine ? "quarantine" : "rollback",
                    cancellationToken);

                try
                {
                    activity.Update(
                        "Installing staged output",
                        "Moving/copying the staged output into the library. Cross-filesystem copies are written to a temporary file beside the destination first.",
                        0,
                        stagedInfo.Length);

                    await MoveFileSafelyAsync(
                        stagingPath,
                        originalPath,
                        "Installing staged output",
                        "library",
                        cancellationToken);
                }
                catch
                {
                    if (!File.Exists(originalPath) && File.Exists(backupPath))
                    {
                        activity.Update(
                            "Rolling back original",
                            "Replacement failed. Restoring the protected original to the library.",
                            0,
                            new FileInfo(backupPath).Length);

                        try
                        {
                            await MoveFileSafelyAsync(
                                backupPath,
                                originalPath,
                                "Rolling back original",
                                "library rollback",
                                CancellationToken.None);
                        }
                        catch (Exception rollbackEx)
                        {
                            logger.LogCritical(
                                rollbackEx,
                                "CRITICAL: replacement failed and rollback also failed for media {MediaId}. Backup remains at {BackupPath}; original path {OriginalPath}",
                                media.Id, backupPath, originalPath);
                        }
                    }

                    throw;
                }

                activity.Update("Verifying replacement", "Checking the completed library file before cleanup.");
                var replacementInfo = new FileInfo(originalPath);

                if (!replacementInfo.Exists)
                    throw new IOException($"Replacement file is missing after install: {originalPath}");

                if (replacementInfo.Length != stagedInfo.Length)
                    throw new IOException($"Replacement size verification failed. Expected {stagedInfo.Length} bytes, found {replacementInfo.Length} bytes.");

                activity.Update("Cleaning staging", "Removing completion marker and temporary rollback artifacts.");
                TryDelete(markerPath);

                if (!keepOriginalsQuarantine)
                    TryDelete(backupPath);

                TryDeleteEmptyDirectories(Path.GetDirectoryName(stagingPath), storageOptions.Value.StagingRoot);

                activity.Update("Updating database", "Recording replacement history and resetting probe/plan state.");

                var firstOriginalSize = stats.OriginalSizeBytes ?? originalInfo.Length;
                var latestSaved = Math.Max(0, firstOriginalSize - replacementInfo.Length);

                stats.OutputSizeBytes = replacementInfo.Length;
                stats.TotalSavedBytes = latestSaved;
                stats.ReplacedOriginal = true;
                stats.ReplacementBackupPath = keepOriginalsQuarantine ? backupPath : null;
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
                    BackupPath = keepOriginalsQuarantine ? backupPath : null,
                    Message = keepOriginalsQuarantine
                        ? "Staged output replaced original; original moved to quarantine."
                        : "Staged output replaced original; temporary rollback copy deleted."
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

                await ResolveReplaceFailureReviewsAsync(media.Id, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                var successMessage = keepOriginalsQuarantine
                    ? "Original replaced successfully. The old file is in quarantine and the media item needs a fresh probe before further work."
                    : "Original replaced successfully. The temporary rollback copy was deleted and the media item needs a fresh probe before further work.";

                activity.Complete(true, successMessage);

                logger.LogInformation(
                    "Server replacement complete: MediaId={MediaId}; Media={Media}; NewBytes={NewBytes}; SavedThisReplaceBytes={SavedThisReplaceBytes}; TotalSavedBytes={TotalSavedBytes}",
                    media.Id, media.RelativePath, replacementInfo.Length,
                    Math.Max(0, originalInfo.Length - replacementInfo.Length), latestSaved);

                return new ReplaceMediaResultDto
                {
                    MediaId = media.Id,
                    Accepted = true,
                    Replaced = true,
                    Message = successMessage,
                    MediaStatus = media.Status,
                    OriginalPath = originalPath,
                    StagingPath = stagingPath,
                    BackupPath = keepOriginalsQuarantine ? backupPath : null,
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

                await UpsertReplaceFailureReviewAsync(
                    media.Id, ex.Message, stagingPath, originalPath, backupPath, cancellationToken);

                await db.SaveChangesAsync(cancellationToken);
                activity.Complete(false, $"Replacement failed: {ex.Message}");
                return Reject(media.Id, $"Replace failed: {ex.Message}", media.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activityStarted)
                activity.Complete(false, "Replacement cancelled because the server is stopping or the request was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            if (activityStarted)
                activity.Complete(false, $"Replacement failed before filesystem swap: {ex.Message}");
            throw;
        }
        finally
        {
            activity.ReplacementGate.Release();
        }
    }

    private ReplaceMediaResultDto FinishRejected(long mediaId, string message, MediaStatus? status)
    {
        activity.Complete(false, message);
        return Reject(mediaId, message, status);
    }

    private async Task MoveFileSafelyAsync(
        string source,
        string destination,
        string stage,
        string destinationDescription,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"Source file does not exist: {source}", source);

        if (File.Exists(destination))
            throw new IOException($"Destination already exists: {destination}");

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        try
        {
            File.Move(source, destination);
            var movedBytes = new FileInfo(destination).Length;
            activity.Update(stage, $"Moved to {destinationDescription} on the same filesystem.", movedBytes, movedBytes);

            logger.LogInformation(
                "Server file move completed without copy: Stage={Stage}; Source={Source}; Destination={Destination}; Bytes={Bytes}",
                stage, source, destination, movedBytes);
            return;
        }
        catch (IOException moveEx)
        {
            logger.LogInformation(
                moveEx,
                "Direct move unavailable; using verified streamed copy: Stage={Stage}; Source={Source}; Destination={Destination}",
                stage, source, destination);
        }

        var tempDestination = destination + $".transcoder-copy-{Guid.NewGuid():N}.partial";
        TryDelete(tempDestination);

        try
        {
            var sourceBytes = new FileInfo(source).Length;
            activity.Update(stage, $"Copying to {destinationDescription}: {FormatBytes(0)} / {FormatBytes(sourceBytes)}", 0, sourceBytes);

            await CopyFileWithProgressAsync(
                source, tempDestination, stage, destinationDescription, cancellationToken);

            activity.Update(
                $"Verifying {destinationDescription} copy",
                $"Verifying {FormatBytes(sourceBytes)} copied to temporary destination.");

            var tempInfo = new FileInfo(tempDestination);
            if (!tempInfo.Exists || tempInfo.Length != sourceBytes)
                throw new IOException(
                    $"Copy verification failed for {tempDestination}. Expected {sourceBytes} bytes, found {(tempInfo.Exists ? tempInfo.Length : -1)} bytes.");

            File.Move(tempDestination, destination);

            var destinationInfo = new FileInfo(destination);
            if (!destinationInfo.Exists || destinationInfo.Length != sourceBytes)
                throw new IOException(
                    $"Destination verification failed for {destination}. Expected {sourceBytes} bytes, found {(destinationInfo.Exists ? destinationInfo.Length : -1)} bytes.");

            File.Delete(source);

            activity.Update(stage, $"Copied and verified {FormatBytes(sourceBytes)} to {destinationDescription}.", sourceBytes, sourceBytes);

            logger.LogInformation(
                "Server streamed move complete: Stage={Stage}; Source={Source}; Destination={Destination}; Bytes={Bytes}",
                stage, source, destination, sourceBytes);
        }
        catch
        {
            TryDelete(tempDestination);
            throw;
        }
    }

    private async Task CopyFileWithProgressAsync(
        string source,
        string destination,
        string stage,
        string destinationDescription,
        CancellationToken cancellationToken)
    {
        var totalBytes = new FileInfo(source).Length;
        long copiedBytes = 0;

        var clock = Stopwatch.StartNew();
        var lastActivityUpdate = TimeSpan.Zero;
        var lastProgressLog = TimeSpan.Zero;

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[CopyBufferSize];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copiedBytes += read;

            var elapsed = clock.Elapsed;

            if (elapsed - lastActivityUpdate >= ActivityUpdateInterval || copiedBytes == totalBytes)
            {
                activity.Update(
                    stage,
                    $"Copying to {destinationDescription}: {FormatBytes(copiedBytes)} / {FormatBytes(totalBytes)}",
                    copiedBytes,
                    totalBytes);
                lastActivityUpdate = elapsed;
            }

            if (elapsed - lastProgressLog >= ProgressLogInterval || copiedBytes == totalBytes)
            {
                var percent = totalBytes > 0 ? copiedBytes * 100d / totalBytes : 100d;
                var speed = elapsed.TotalSeconds > 0 ? copiedBytes / elapsed.TotalSeconds : 0;

                logger.LogInformation(
                    "Server copy progress: Stage={Stage}; Destination={DestinationDescription}; Copied={CopiedBytes}; Total={TotalBytes}; Progress={Progress:n1}%; ThroughputBytesPerSecond={Throughput:n0}",
                    stage, destinationDescription, copiedBytes, totalBytes, percent, speed);

                lastProgressLog = elapsed;
            }
        }

        await output.FlushAsync(cancellationToken);

        if (copiedBytes != totalBytes)
            throw new IOException($"Copy ended at {copiedBytes} bytes but source size is {totalBytes} bytes.");
    }

    public static bool IsReplaceFailureReason(string? reason)
        => string.Equals(reason, "Replace original failed.", StringComparison.OrdinalIgnoreCase);

    private async Task UpsertReplaceFailureReviewAsync(
        long mediaId, string error, string stagingPath, string originalPath, string backupPath,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            error,
            stagingPath,
            originalPath,
            backupPath,
            retryable = true,
            failedUtc = DateTime.UtcNow
        }, JsonOptions);

        var review = await db.ReviewItems.FirstOrDefaultAsync(
            x => x.MediaItemId == mediaId && !x.Resolved && x.Reason == "Replace original failed.",
            cancellationToken);

        if (review is null)
        {
            db.ReviewItems.Add(new ReviewItemEntity
            {
                MediaItemId = mediaId,
                ReviewType = ReviewType.OutputValidationWarning,
                Severity = ReviewSeverity.Blocking,
                Reason = "Replace original failed.",
                DetailsJson = details
            });
            return;
        }

        review.ReviewType = ReviewType.OutputValidationWarning;
        review.Severity = ReviewSeverity.Blocking;
        review.DetailsJson = details;
    }

    private async Task ResolveReplaceFailureReviewsAsync(long mediaId, CancellationToken cancellationToken)
    {
        var reviews = await db.ReviewItems
            .Where(x => x.MediaItemId == mediaId && !x.Resolved && x.Reason == "Replace original failed.")
            .ToListAsync(cancellationToken);

        foreach (var review in reviews)
        {
            review.Resolved = true;
            review.ResolvedUtc = DateTime.UtcNow;
        }
    }

    public async Task<ReplaceLibraryResultDto> ReplaceLibraryAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var ids = await db.MediaItems.AsNoTracking()
            .Where(x => x.LibraryId == libraryId &&
                (x.Status == MediaStatus.StagedCleaned || x.Status == MediaStatus.Staged || x.Status == MediaStatus.Approved))
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

    private static string BuildTemporaryRollbackPath(MediaItemEntity media)
    {
        var directory = Path.GetDirectoryName(media.FullPath) ?? string.Empty;
        var name = Path.GetFileName(media.FullPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        return Path.Combine(directory, $".{name}.transcoder-rollback-{stamp}.tmp");
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
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

            while (!string.IsNullOrWhiteSpace(current)
                   && current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && !current.Equals(root, StringComparison.OrdinalIgnoreCase))
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
