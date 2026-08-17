namespace Transcoder.Server.Services;

public sealed class ServerActivityState
{
    private readonly object _sync = new();
    private ServerActivityOperation? _current;
    private ServerActivityOperation? _last;

    public SemaphoreSlim ReplacementGate { get; } = new(1, 1);

    public ServerActivitySnapshot Snapshot()
    {
        lock (_sync)
            return new ServerActivitySnapshot(_current?.Clone(), _last?.Clone());
    }

    public void Start(ServerActivityOperation operation)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            operation.StartedUtc = now;
            operation.StageStartedUtc = now;
            operation.UpdatedUtc = now;
            operation.CompletedUtc = null;
            operation.Success = null;
            _current = operation;
        }
    }

    public void Update(string stage, string? message = null, long? bytesProcessed = null, long? totalBytes = null)
    {
        lock (_sync)
        {
            if (_current is null) return;
            var now = DateTime.UtcNow;
            if (!string.Equals(_current.Stage, stage, StringComparison.Ordinal))
            {
                _current.Stage = stage;
                _current.StageStartedUtc = now;
                _current.BytesProcessed = bytesProcessed;
                _current.TotalBytes = totalBytes;
            }
            else
            {
                if (bytesProcessed is not null) _current.BytesProcessed = bytesProcessed;
                if (totalBytes is not null) _current.TotalBytes = totalBytes;
            }
            if (message is not null) _current.Message = message;
            _current.UpdatedUtc = now;
        }
    }

    public void Complete(bool success, string message)
    {
        lock (_sync)
        {
            if (_current is null) return;
            var now = DateTime.UtcNow;
            _current.Success = success;
            _current.Message = message;
            _current.Stage = success ? "Complete" : "Stopped";
            _current.UpdatedUtc = now;
            _current.CompletedUtc = now;
            _last = _current.Clone();
            _current = null;
        }
    }

    public void Clear(string message = "Activity cleared.")
    {
        lock (_sync)
        {
            if (_current is not null)
            {
                var now = DateTime.UtcNow;
                _current.Success = false;
                _current.Message = message;
                _current.Stage = "Stopped";
                _current.UpdatedUtc = now;
                _current.CompletedUtc = now;
                _last = _current.Clone();
            }
            _current = null;
        }
    }
}

public sealed record ServerActivitySnapshot(ServerActivityOperation? Current, ServerActivityOperation? Last);

public sealed class ServerActivityOperation
{
    public string Operation { get; set; } = "ServerWork";
    public long? MediaId { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string? RelativePath { get; set; }
    public string? OriginalPath { get; set; }
    public string? StagingPath { get; set; }
    public string Stage { get; set; } = "Starting";
    public string? Message { get; set; }
    public long? BytesProcessed { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime StageStartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool? Success { get; set; }

    public ServerActivityOperation Clone() => new()
    {
        Operation = Operation,
        MediaId = MediaId,
        LibraryId = LibraryId,
        LibraryName = LibraryName,
        RelativePath = RelativePath,
        OriginalPath = OriginalPath,
        StagingPath = StagingPath,
        Stage = Stage,
        Message = Message,
        BytesProcessed = BytesProcessed,
        TotalBytes = TotalBytes,
        StartedUtc = StartedUtc,
        StageStartedUtc = StageStartedUtc,
        UpdatedUtc = UpdatedUtc,
        CompletedUtc = CompletedUtc,
        Success = Success
    };
}
