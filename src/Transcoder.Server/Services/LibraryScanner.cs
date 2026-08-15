using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public sealed class LibraryScanner(TranscoderDbContext db, ILogger<LibraryScanner> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task ScanAsync(int libraryId, bool force, bool createProbeJobs, CancellationToken cancellationToken = default)
    {
        var library = await db.Libraries.FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (library is null) return;

        library.IsScanning = true;
        library.ScanStartedUtc = DateTime.UtcNow;
        library.ScanCompletedUtc = null;
        library.ScanLastError = null;
        library.FoldersScanned = 0;
        library.FilesDiscovered = 0;
        library.MediaItemsCreated = 0;
        library.MediaItemsUpdated = 0;
        library.ProbeJobsCreated = 0;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var policy = DeserializePolicy(library.PolicyJson);
            if (!Directory.Exists(library.RootPath))
                throw new DirectoryNotFoundException($"Library root path does not exist: {library.RootPath}");

            var root = new DirectoryInfo(library.RootPath);
            var ignoreNames = new HashSet<string>(policy.IgnoreFolderNames, StringComparer.OrdinalIgnoreCase);
            var allowedExtensions = new HashSet<string>(policy.AllowedExtensions, StringComparer.OrdinalIgnoreCase);
            var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reconciliationSafe = true;

            foreach (var file in EnumerateFiles(
                         root,
                         ignoreNames,
                         path =>
                         {
                             reconciliationSafe = false;
                             logger.LogWarning(
                                 "Library {LibraryId} could not enumerate {Path}; missing-media reconciliation will be skipped for this scan.",
                                 library.Id,
                                 path);
                         },
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!allowedExtensions.Contains(file.Extension))
                    continue;

                var relativePath = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                seenRelativePaths.Add(relativePath);

                library.FilesDiscovered++;
                var result = await UpsertMediaFileAsync(library, policy, file.FullName, force, createProbeJobs, cancellationToken);
                if (result.Created) library.MediaItemsCreated++;
                if (result.Updated) library.MediaItemsUpdated++;
                if (result.ProbeJobCreated) library.ProbeJobsCreated++;

                await db.SaveChangesAsync(cancellationToken);
            }

            if (reconciliationSafe)
            {
                var missing = await ReconcileMissingMediaAsync(library, seenRelativePaths, cancellationToken);
                if (missing.MarkedMissing > 0 || missing.CancelledJobs > 0)
                {
                    logger.LogInformation(
                        "Library scan reconciled missing media: Library={LibraryId}; MissingMedia={MissingMedia}; CancelledQueuedJobs={CancelledQueuedJobs}",
                        library.Id,
                        missing.MarkedMissing,
                        missing.CancelledJobs);
                }
            }
            else
            {
                logger.LogWarning(
                    "Library {LibraryId} scan completed with traversal errors; no MediaItem rows were marked Missing.",
                    library.Id);
            }

            library.IsScanning = false;
            library.ScanCompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan library {LibraryId}", libraryId);
            library.IsScanning = false;
            library.ScanLastError = ex.Message;
            library.ScanCompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<WatchedFileProcessResult> ProcessFileAsync(int libraryId, string fullPath, bool force = false, bool createProbeJobs = true, CancellationToken cancellationToken = default)
    {
        var library = await db.Libraries.FirstOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (library is null || !library.Enabled)
            return WatchedFileProcessResult.Ignored("Library not found or disabled.");

        var policy = DeserializePolicy(library.PolicyJson);
        var root = Path.GetFullPath(library.RootPath);
        var path = Path.GetFullPath(fullPath);

        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return WatchedFileProcessResult.Ignored("File is outside the library root.");

        if (!File.Exists(path))
        {
            await MarkWatchedFileMissingAsync(libraryId, root, path, cancellationToken);
            return WatchedFileProcessResult.Ignored("File no longer exists.");
        }

        var file = new FileInfo(path);
        if (!new HashSet<string>(policy.AllowedExtensions, StringComparer.OrdinalIgnoreCase).Contains(file.Extension))
            return WatchedFileProcessResult.Ignored("File extension is not allowed.");

        if (IsInIgnoredFolder(root, path, policy))
            return WatchedFileProcessResult.Ignored("File is inside an ignored folder.");

        if (policy.Watch.IgnoreTemporaryExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            return WatchedFileProcessResult.Ignored("File extension is temporary/ignored.");

        var result = await UpsertMediaFileAsync(library, policy, file.FullName, force, createProbeJobs, cancellationToken);
        library.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new WatchedFileProcessResult(true, result.Created, result.Updated, result.ProbeJobCreated, null);
    }

    private async Task<MediaUpsertResult> UpsertMediaFileAsync(LibraryEntity library, LibraryPolicyDto policy, string fullPath, bool force, bool createProbeJobs, CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(library.RootPath);
        var path = Path.GetFullPath(fullPath);
        var relativePath = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
        var file = new FileInfo(path);
        var fileSize = file.Length;
        var lastModifiedUtc = file.LastWriteTimeUtc;

        // Missing items are hidden by the DbContext query filter, so deliberately bypass it here
        // to restore the same row (and its history) when a file reappears.
        var existing = await db.MediaItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.LibraryId == library.Id && x.RelativePath == relativePath, cancellationToken);
        var created = false;
        var updated = false;
        var shouldProbe = force;

        if (existing is null)
        {
            existing = new MediaItemEntity
            {
                LibraryId = library.Id,
                RelativePath = relativePath,
                FullPath = file.FullName,
                FileSizeBytes = fileSize,
                LastModifiedUtc = lastModifiedUtc,
                Status = MediaStatus.New
            };
            db.MediaItems.Add(existing);
            created = true;
            shouldProbe = true;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (existing.Status == MediaStatus.Missing
                 || existing.FileSizeBytes != fileSize
                 || existing.LastModifiedUtc != lastModifiedUtc
                 || force)
        {
            var wasMissing = existing.Status == MediaStatus.Missing;
            existing.FullPath = file.FullName;
            existing.FileSizeBytes = fileSize;
            existing.LastModifiedUtc = lastModifiedUtc;
            existing.ProbeJson = null;
            existing.Status = MediaStatus.New;
            existing.UpdatedUtc = DateTime.UtcNow;
            updated = true;
            shouldProbe = true;

            if (wasMissing)
                logger.LogInformation("Previously missing media restored: MediaItemId={MediaItemId}; Path={Path}", existing.Id, relativePath);
        }

        var probeJobCreated = false;
        if (createProbeJobs && shouldProbe)
        {
            var alreadyQueued = await db.Jobs.AnyAsync(j =>
                j.MediaItemId == existing.Id
                && j.JobType == JobType.Probe
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Leased || j.Status == JobStatus.Running), cancellationToken);

            if (!alreadyQueued)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    libraryId = library.Id,
                    mediaId = existing.Id,
                    inputPath = existing.FullPath,
                    fileSizeBytes = existing.FileSizeBytes,
                    lastModifiedUtc = existing.LastModifiedUtc
                }, JsonOptions);

                db.Jobs.Add(new JobEntity
                {
                    JobType = JobType.Probe,
                    Status = JobStatus.Queued,
                    LibraryId = library.Id,
                    MediaItemId = existing.Id,
                    PayloadJson = payload
                });
                existing.Status = MediaStatus.ProbeQueued;
                probeJobCreated = true;
            }
        }

        return new MediaUpsertResult(created, updated, probeJobCreated);
    }

    private async Task<MissingMediaReconciliationResult> ReconcileMissingMediaAsync(
        LibraryEntity library,
        HashSet<string> seenRelativePaths,
        CancellationToken cancellationToken)
    {
        var mediaItems = await db.MediaItems.IgnoreQueryFilters()
            .Where(x => x.LibraryId == library.Id && x.Status != MediaStatus.Missing)
            .ToListAsync(cancellationToken);

        var missingItems = mediaItems
            .Where(x => !seenRelativePaths.Contains(x.RelativePath.Replace('\\', '/')))
            // A policy change can make an existing file no longer enumerable/eligible without
            // actually deleting it. Only mark the row Missing when the source path is truly gone.
            .Where(x => !File.Exists(x.FullPath))
            .ToList();

        if (missingItems.Count == 0)
            return new MissingMediaReconciliationResult(0, 0);

        var now = DateTime.UtcNow;
        var missingIds = missingItems.Select(x => x.Id).ToHashSet();

        foreach (var media in missingItems)
        {
            media.Status = MediaStatus.Missing;
            media.UpdatedUtc = now;
        }

        var queuedJobs = await db.Jobs
            .Where(x => x.MediaItemId != null
                        && missingIds.Contains(x.MediaItemId.Value)
                        && x.Status == JobStatus.Queued)
            .ToListAsync(cancellationToken);

        foreach (var job in queuedJobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedUtc = now;
            job.LastError = "Source media is no longer present in the library.";
            job.LastMessage = "Cancelled by library scan missing-media reconciliation.";
        }

        await db.SaveChangesAsync(cancellationToken);
        return new MissingMediaReconciliationResult(missingItems.Count, queuedJobs.Count);
    }

    private async Task MarkWatchedFileMissingAsync(int libraryId, string rootPath, string fullPath, CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        var media = await db.MediaItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.LibraryId == libraryId && x.RelativePath == relativePath, cancellationToken);

        if (media is null || media.Status == MediaStatus.Missing)
            return;

        media.Status = MediaStatus.Missing;
        media.UpdatedUtc = DateTime.UtcNow;

        var queuedJobs = await db.Jobs
            .Where(x => x.MediaItemId == media.Id && x.Status == JobStatus.Queued)
            .ToListAsync(cancellationToken);

        foreach (var job in queuedJobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            job.LastError = "Source media is no longer present in the library.";
            job.LastMessage = "Cancelled after filesystem watcher detected removal.";
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Filesystem watcher marked media missing: MediaItemId={MediaItemId}; Path={Path}; CancelledQueuedJobs={CancelledQueuedJobs}",
            media.Id,
            media.RelativePath,
            queuedJobs.Count);
    }

    private IEnumerable<FileInfo> EnumerateFiles(
        DirectoryInfo root,
        HashSet<string> ignoreNames,
        Action<string> onTraversalError,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            if (ignoreNames.Contains(directory.Name))
                continue;

            try
            {
                var trackedLibrary = db.Libraries.Local.FirstOrDefault(x => x.RootPath == root.FullName);
                if (trackedLibrary is not null)
                    trackedLibrary.FoldersScanned++;
            }
            catch
            {
                // Scanning should not fail just because progress tracking failed.
            }

            IEnumerable<DirectoryInfo> directories = [];
            IEnumerable<FileInfo> files = [];
            try
            {
                directories = directory.EnumerateDirectories();
                files = directory.EnumerateFiles();
            }
            catch (UnauthorizedAccessException)
            {
                onTraversalError(directory.FullName);
                continue;
            }
            catch (IOException)
            {
                onTraversalError(directory.FullName);
                continue;
            }

            foreach (var child in directories)
                stack.Push(child);

            foreach (var file in files)
                yield return file;
        }
    }

    private static bool IsInIgnoredFolder(string rootPath, string filePath, LibraryPolicyDto policy)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => policy.IgnoreFolderNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto();
        }
        catch
        {
            return new LibraryPolicyDto();
        }
    }

    private sealed record MediaUpsertResult(bool Created, bool Updated, bool ProbeJobCreated);
    private sealed record MissingMediaReconciliationResult(int MarkedMissing, int CancelledJobs);
}

public sealed record WatchedFileProcessResult(bool Processed, bool Created, bool Updated, bool ProbeJobCreated, string? IgnoredReason)
{
    public static WatchedFileProcessResult Ignored(string reason) => new(false, false, false, false, reason);
}
