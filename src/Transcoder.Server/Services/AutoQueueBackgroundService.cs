using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Transcoder.Server.Services;

public sealed class AutoQueueBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AutoQueueBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var planner = scope.ServiceProvider.GetRequiredService<TranscodePlanService>();
                var result = await planner.ReconcileAutomaticQueuesAsync(stoppingToken);

                if (result.QueuedCleanup > 0 || result.QueuedTranscode > 0)
                {
                    logger.LogInformation("Automation queued {CleanupCount} cleanup and {TranscodeCount} transcode job(s).", result.QueuedCleanup, result.QueuedTranscode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automatic queue reconciliation failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
