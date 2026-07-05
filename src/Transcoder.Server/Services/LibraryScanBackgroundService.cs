namespace Transcoder.Server.Services;

public sealed class LibraryScanBackgroundService(IServiceProvider services, ScanQueue queue, ILogger<LibraryScanBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var libraryId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
                await scanner.ScanAsync(libraryId, force: false, createProbeJobs: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Library scan failed for library {LibraryId}", libraryId);
            }
        }
    }
}
