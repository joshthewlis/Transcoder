using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;

namespace Transcoder.Server.Services;

public sealed class AutoReplaceBackgroundService(
    IServiceScopeFactory scopeFactory,
    ServerActivityState activity,
    ILogger<AutoReplaceBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                activity.Clear("Server is shutting down.");
            }
            catch (Exception ex)
            {
                activity.Complete(false, $"Auto replace loop failed: {ex.Message}");
                logger.LogError(ex, "Auto replace loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        if (mode != ProcessingMode.ReplaceApproved)
            return;

        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
        var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);
        if (!activeHours.AllowStagedWork)
        {
            logger.LogDebug("Auto-replace paused by active hours: {Message}", activeHours.Message);
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
        var candidates = await db.MediaItems.AsNoTracking()
            .Include(x => x.Library)
            .Where(x => x.Library != null
                && (x.Status == MediaStatus.StagedCleaned
                    || x.Status == MediaStatus.Staged
                    || x.Status == MediaStatus.Approved))
            .OrderBy(x => x.UpdatedUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        var replacement = scope.ServiceProvider.GetRequiredService<ReplacementService>();
        foreach (var candidate in candidates)
        {
            if (candidate.Library is null || !LibraryAllowsReplace(candidate.Library.PolicyJson))
                continue;

            long? stagedBytes = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate.StagingPath) && File.Exists(candidate.StagingPath))
                    stagedBytes = new FileInfo(candidate.StagingPath).Length;
            }
            catch
            {
                // Activity display should never prevent replacement from being attempted.
            }

            activity.Start(new ServerActivityOperation
            {
                Operation = "ReplaceOriginal",
                MediaId = candidate.Id,
                LibraryId = candidate.LibraryId,
                LibraryName = candidate.Library.Name,
                RelativePath = candidate.RelativePath,
                OriginalPath = candidate.FullPath,
                StagingPath = candidate.StagingPath,
                Stage = "Replacing original",
                Message = "ReplacementService is moving the current library file to rollback/quarantine and installing the staged output.",
                TotalBytes = stagedBytes
            });

            logger.LogInformation(
                "Server replacement started: MediaId={MediaId}; Library={Library}; Media={Media}; Original={Original}; Staging={Staging}; StagedBytes={StagedBytes}",
                candidate.Id,
                candidate.Library.Name,
                candidate.RelativePath,
                candidate.FullPath,
                candidate.StagingPath,
                stagedBytes);

            try
            {
                var result = await replacement.ReplaceMediaAsync(candidate.Id, cancellationToken);
                if (result.Replaced)
                {
                    activity.Complete(
                        true,
                        $"Replacement complete. Original retained safely during the swap; final size {FormatBytes(result.ReplacementSizeBytes)}.");
                    logger.LogInformation(
                        "Auto-replaced original for media {MediaId}: {Path}; ReplacementBytes={ReplacementBytes}; TotalSavedBytes={TotalSavedBytes}",
                        candidate.Id,
                        result.OriginalPath,
                        result.ReplacementSizeBytes,
                        result.TotalSavedBytes);
                }
                else
                {
                    activity.Complete(false, result.Message ?? "Replacement was skipped.");
                    logger.LogInformation(
                        "Auto-replace did not replace media {MediaId}: {Message}",
                        candidate.Id,
                        result.Message);
                }
            }
            catch (Exception ex)
            {
                activity.Complete(false, $"Replacement failed: {ex.Message}");
                throw;
            }
        }
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "unknown";
        var value = (double)bytes.Value;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static bool LibraryAllowsReplace(string policyJson)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<LibraryPolicyDto>(policyJson, JsonOptions)
                ?? new LibraryPolicyDto();
            return policy.Output.ReplaceOriginals;
        }
        catch
        {
            return false;
        }
    }
}
