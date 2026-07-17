using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class StorageMapAutoImportService(
    IServiceScopeFactory scopeFactory,
    IOptions<ActiveHoursOptions> activeHoursOptions,
    ILogger<StorageMapAutoImportService> logger) : BackgroundService
{
    private string? _lastSuccessfulLocalDate;
    private DateTimeOffset? _nextRetryUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryRunScheduledImportAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var retryMinutes = 30;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
                    retryMinutes = Math.Max(1, (await settings.GetStorageRuntimeSettingsAsync(stoppingToken)).AutoImportStorageMapRetryMinutes);
                }
                catch
                {
                    // Keep the background service alive even if settings lookup fails after the real import exception.
                }

                _nextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(retryMinutes);
                logger.LogWarning(ex, "Scheduled storage map import failed. Retrying in {RetryMinutes} minute(s).", retryMinutes);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TryRunScheduledImportAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        var runtime = await settings.GetStorageRuntimeSettingsAsync(cancellationToken);

        if (!runtime.AutoImportStorageMap)
            return;

        var nowLocal = GetLocalNow(activeHoursOptions.Value.TimeZoneId);
        var localDate = nowLocal.ToString("yyyy-MM-dd");
        if (string.Equals(_lastSuccessfulLocalDate, localDate, StringComparison.Ordinal))
            return;

        var scheduled = ParseTime(runtime.AutoImportStorageMapTime);
        if (nowLocal.TimeOfDay < scheduled)
            return;

        if (_nextRetryUtc is not null && DateTimeOffset.UtcNow < _nextRetryUtc.Value)
            return;

        var importer = scope.ServiceProvider.GetRequiredService<StorageMapImportService>();
        var result = await importer.ImportAsync(null, cancellationToken);

        _lastSuccessfulLocalDate = localDate;
        _nextRetryUtc = null;

        logger.LogInformation(
            "Scheduled storage map import complete. manifest={ManifestPath}, rows={Rows}, matched={Matched}, unmatched={Unmatched}, ambiguous={Ambiguous}",
            result.ManifestPath,
            result.ManifestRows,
            result.MatchedMedia,
            result.UnmatchedMedia,
            result.AmbiguousMatches);
    }

    private static DateTime GetLocalNow(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return DateTime.Now;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    private static TimeSpan ParseTime(string? value)
    {
        return TimeSpan.TryParse(value, out var parsed)
            ? parsed
            : new TimeSpan(8, 10, 0);
    }
}
