using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;

namespace Transcoder.Server.Services;

public sealed class LibraryWatcherBackgroundService(
    IServiceProvider services,
    ScanQueue scanQueue,
    LibraryWatcherState watcherState,
    ILogger<LibraryWatcherBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly Dictionary<int, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTime> _lastPeriodicScans = [];
    private DateTime _lastWatcherRefreshUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshWatchersAsync(startup: true, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Initial library watcher setup failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastWatcherRefreshUtc > TimeSpan.FromMinutes(2))
                    await RefreshWatchersAsync(startup: false, stoppingToken);

                await ProcessPendingFilesAsync(stoppingToken);
                await QueuePeriodicScansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Library watcher loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
    }

    private async Task RefreshWatchersAsync(bool startup, CancellationToken cancellationToken)
    {
        _lastWatcherRefreshUtc = DateTime.UtcNow;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
        var libraries = await db.Libraries.AsNoTracking().Where(x => x.Enabled).ToListAsync(cancellationToken);
        var enabledIds = new HashSet<int>();

        foreach (var library in libraries)
        {
            var policy = DeserializePolicy(library.PolicyJson);
            if (!policy.Watch.Enabled)
            {
                watcherState.SetWatcherActive(library.Id, library.Name, library.RootPath, watchEnabled: false, active: false, includeSubdirectories: policy.Watch.IncludeSubdirectories);
                continue;
            }

            enabledIds.Add(library.Id);

            if (startup && policy.Watch.ScanOnStartup)
            {
                var queuedUtc = DateTime.UtcNow;
                await scanQueue.QueueAsync(library.Id, cancellationToken);
                _lastPeriodicScans[library.Id] = queuedUtc;
                watcherState.RecordScanQueued(library.Id, queuedUtc, policy.Watch.PeriodicRescanMinutes > 0 ? queuedUtc.AddMinutes(policy.Watch.PeriodicRescanMinutes) : null);
                logger.LogInformation("Queued startup scan for library {LibraryId}. This can take a while on large or slow network libraries.", library.Id);
            }

            if (_watchers.ContainsKey(library.Id))
                continue;

            if (!Directory.Exists(library.RootPath))
            {
                var message = $"Root does not exist: {library.RootPath}";
                watcherState.SetWatcherActive(library.Id, library.Name, library.RootPath, watchEnabled: true, active: false, includeSubdirectories: policy.Watch.IncludeSubdirectories, error: message);
                logger.LogWarning("Cannot watch library {LibraryId}; root does not exist: {RootPath}", library.Id, library.RootPath);
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(library.RootPath)
                {
                    IncludeSubdirectories = policy.Watch.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                watcher.Created += (_, e) => AddPending(library.Id, e.FullPath);
                watcher.Changed += (_, e) => AddPending(library.Id, e.FullPath);
                watcher.Renamed += (_, e) => AddPending(library.Id, e.FullPath);
                watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "File watcher error for library {LibraryId}", library.Id);

                _watchers[library.Id] = watcher;
                watcherState.SetWatcherActive(library.Id, library.Name, library.RootPath, watchEnabled: true, active: true, includeSubdirectories: policy.Watch.IncludeSubdirectories);
                logger.LogInformation("Watching library {LibraryId}: {RootPath} (subdirectories={IncludeSubdirectories})", library.Id, library.RootPath, policy.Watch.IncludeSubdirectories);
            }
            catch (Exception ex)
            {
                watcherState.SetWatcherActive(library.Id, library.Name, library.RootPath, watchEnabled: true, active: false, includeSubdirectories: policy.Watch.IncludeSubdirectories, error: ex.Message);
                logger.LogWarning(ex, "Failed to start watcher for library {LibraryId}: {RootPath}", library.Id, library.RootPath);
            }
        }

        var staleIds = _watchers.Keys.Where(id => !enabledIds.Contains(id)).ToList();
        foreach (var id in staleIds)
        {
            _watchers[id].Dispose();
            _watchers.Remove(id);
            watcherState.Remove(id);
            logger.LogInformation("Stopped watching library {LibraryId}", id);
        }
    }

    private void AddPending(int libraryId, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        // FileSystemWatcher can be noisy, especially on network shares. Ignore directory events and paths
        // that cannot possibly be media files. A periodic/manual scan will catch folder moves/imports.
        try
        {
            if (Directory.Exists(fullPath)) return;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(fullPath))) return;

        var now = DateTime.UtcNow;
        var key = $"{libraryId}|{fullPath}";
        _pending.AddOrUpdate(key,
            _ => new PendingFile(libraryId, fullPath, now),
            (_, existing) => existing with { LastEventUtc = now });

        watcherState.RecordEvent(libraryId, now);
    }

    private async Task ProcessPendingFilesAsync(CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
            return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
        var libraries = await db.Libraries.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        UpdatePendingCounts();

        foreach (var pair in _pending.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = pair.Value;
            if (!libraries.TryGetValue(pending.LibraryId, out var library) || !library.Enabled)
            {
                _pending.TryRemove(pair.Key, out _);
                continue;
            }

            var policy = DeserializePolicy(library.PolicyJson);
            var settle = TimeSpan.FromSeconds(Math.Max(10, policy.Watch.SettleSeconds));
            var minAge = TimeSpan.FromSeconds(Math.Max(0, policy.Watch.MinimumFileAgeSeconds));
            var now = DateTime.UtcNow;

            if (now - pending.FirstSeenUtc < minAge || now - pending.LastEventUtc < settle)
                continue;

            if (!File.Exists(pending.Path))
            {
                _pending.TryRemove(pair.Key, out _);
                continue;
            }

            FileInfo file;
            try
            {
                file = new FileInfo(pending.Path);
                if (!policy.AllowedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                    || policy.Watch.IgnoreTemporaryExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    _pending.TryRemove(pair.Key, out _);
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (pending.LastSizeBytes is null || pending.LastModifiedUtc is null)
            {
                _pending[pair.Key] = pending with { LastSizeBytes = file.Length, LastModifiedUtc = file.LastWriteTimeUtc, LastEventUtc = now };
                continue;
            }

            if (pending.LastSizeBytes != file.Length || pending.LastModifiedUtc != file.LastWriteTimeUtc)
            {
                _pending[pair.Key] = pending with { LastSizeBytes = file.Length, LastModifiedUtc = file.LastWriteTimeUtc, LastEventUtc = now };
                continue;
            }

            try
            {
                var result = await scanner.ProcessFileAsync(pending.LibraryId, pending.Path, force: false, createProbeJobs: true, cancellationToken);
                _pending.TryRemove(pair.Key, out _);
                watcherState.RecordProcessed(pending.LibraryId, DateTime.UtcNow);
                if (result.Processed)
                {
                    logger.LogInformation("Processed watched file {Path}: created={Created}, updated={Updated}, probeJobCreated={ProbeJobCreated}", pending.Path, result.Created, result.Updated, result.ProbeJobCreated);
                }
                else
                {
                    logger.LogDebug("Ignored watched file {Path}: {Reason}", pending.Path, result.IgnoredReason);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process watched file {Path}", pending.Path);
            }
        }

        UpdatePendingCounts();
    }

    private void UpdatePendingCounts()
    {
        var counts = _pending.Values.GroupBy(x => x.LibraryId).ToDictionary(x => x.Key, x => x.Count());
        foreach (var status in watcherState.GetAll())
            watcherState.SetPendingCount(status.LibraryId, counts.TryGetValue(status.LibraryId, out var count) ? count : 0);
    }

    private async Task QueuePeriodicScansAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
        var libraries = await db.Libraries.AsNoTracking().Where(x => x.Enabled).ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;

        foreach (var library in libraries)
        {
            var policy = DeserializePolicy(library.PolicyJson);
            if (!policy.Watch.Enabled || policy.Watch.PeriodicRescanMinutes <= 0)
                continue;

            if (!_lastPeriodicScans.TryGetValue(library.Id, out var last))
            {
                _lastPeriodicScans[library.Id] = now;
                watcherState.SetNextPeriodicScan(library.Id, now.AddMinutes(policy.Watch.PeriodicRescanMinutes));
                continue;
            }

            if (now - last < TimeSpan.FromMinutes(policy.Watch.PeriodicRescanMinutes))
                continue;

            _lastPeriodicScans[library.Id] = now;
            await scanQueue.QueueAsync(library.Id, cancellationToken);
            watcherState.RecordScanQueued(library.Id, now, now.AddMinutes(policy.Watch.PeriodicRescanMinutes));
            logger.LogInformation("Queued periodic scan for library {LibraryId}", library.Id);
        }
    }

    private static LibraryPolicyDto DeserializePolicy(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(json, JsonOptions) ?? new LibraryPolicyDto(); }
        catch { return new LibraryPolicyDto(); }
    }

    private sealed record PendingFile(int LibraryId, string Path, DateTime FirstSeenUtc)
    {
        public DateTime LastEventUtc { get; init; } = FirstSeenUtc;
        public long? LastSizeBytes { get; init; }
        public DateTime? LastModifiedUtc { get; init; }
    }
}
