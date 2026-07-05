using System.Collections.Concurrent;
using Transcoder.Contracts;

namespace Transcoder.Server.Services;

public sealed class LibraryWatcherState
{
    private readonly ConcurrentDictionary<int, LibraryWatchStatusDto> _statuses = new();

    public IReadOnlyCollection<LibraryWatchStatusDto> GetAll() => _statuses.Values.OrderBy(x => x.LibraryName).ToList();

    public LibraryWatchStatusDto? Get(int libraryId) => _statuses.TryGetValue(libraryId, out var status) ? status : null;

    public void Upsert(LibraryWatchStatusDto status)
    {
        _statuses.AddOrUpdate(status.LibraryId, status, (_, existing) => Merge(existing, status));
    }

    public void SetWatcherActive(int libraryId, string name, string rootPath, bool watchEnabled, bool active, bool includeSubdirectories, string? error = null)
    {
        Upsert(new LibraryWatchStatusDto
        {
            LibraryId = libraryId,
            LibraryName = name,
            RootPath = rootPath,
            WatchEnabled = watchEnabled,
            WatcherActive = active,
            IncludeSubdirectories = includeSubdirectories,
            LastError = error
        });
    }

    public void SetPendingCount(int libraryId, int pendingCount)
    {
        _statuses.AddOrUpdate(libraryId,
            _ => new LibraryWatchStatusDto { LibraryId = libraryId, PendingFileCount = pendingCount },
            (_, existing) => { existing.PendingFileCount = pendingCount; return existing; });
    }

    public void RecordEvent(int libraryId, DateTime utc)
    {
        _statuses.AddOrUpdate(libraryId,
            _ => new LibraryWatchStatusDto { LibraryId = libraryId, LastEventUtc = utc },
            (_, existing) => { existing.LastEventUtc = utc; return existing; });
    }

    public void RecordProcessed(int libraryId, DateTime utc)
    {
        _statuses.AddOrUpdate(libraryId,
            _ => new LibraryWatchStatusDto { LibraryId = libraryId, LastProcessedUtc = utc },
            (_, existing) => { existing.LastProcessedUtc = utc; return existing; });
    }

    public void RecordScanQueued(int libraryId, DateTime utc, DateTime? nextPeriodicScanUtc = null)
    {
        _statuses.AddOrUpdate(libraryId,
            _ => new LibraryWatchStatusDto { LibraryId = libraryId, LastScanQueuedUtc = utc, NextPeriodicScanUtc = nextPeriodicScanUtc },
            (_, existing) => { existing.LastScanQueuedUtc = utc; existing.NextPeriodicScanUtc = nextPeriodicScanUtc; return existing; });
    }

    public void SetNextPeriodicScan(int libraryId, DateTime? nextPeriodicScanUtc)
    {
        _statuses.AddOrUpdate(libraryId,
            _ => new LibraryWatchStatusDto { LibraryId = libraryId, NextPeriodicScanUtc = nextPeriodicScanUtc },
            (_, existing) => { existing.NextPeriodicScanUtc = nextPeriodicScanUtc; return existing; });
    }

    public void Remove(int libraryId) => _statuses.TryRemove(libraryId, out _);

    private static LibraryWatchStatusDto Merge(LibraryWatchStatusDto existing, LibraryWatchStatusDto incoming)
    {
        existing.LibraryName = string.IsNullOrWhiteSpace(incoming.LibraryName) ? existing.LibraryName : incoming.LibraryName;
        existing.RootPath = string.IsNullOrWhiteSpace(incoming.RootPath) ? existing.RootPath : incoming.RootPath;
        existing.WatchEnabled = incoming.WatchEnabled;
        existing.WatcherActive = incoming.WatcherActive;
        existing.IncludeSubdirectories = incoming.IncludeSubdirectories;
        existing.LastError = incoming.LastError ?? existing.LastError;
        return existing;
    }
}
