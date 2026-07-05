using System.Threading.Channels;

namespace Transcoder.Server.Services;

public sealed class ScanQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public ValueTask QueueAsync(int libraryId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(libraryId, cancellationToken);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
