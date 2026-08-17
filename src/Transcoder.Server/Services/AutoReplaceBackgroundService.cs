using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;

namespace Transcoder.Server.Services;

public sealed class AutoReplaceBackgroundService(
    IServiceScopeFactory scopeFactory,
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
            }
            catch (Exception ex)
            {
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
            .Where(x => x.Library != null &&
                (x.Status == MediaStatus.StagedCleaned || x.Status == MediaStatus.Staged || x.Status == MediaStatus.Approved))
            .OrderBy(x => x.UpdatedUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        var replacement = scope.ServiceProvider.GetRequiredService<ReplacementService>();
        foreach (var candidate in candidates)
        {
            if (candidate.Library is null || !LibraryAllowsReplace(candidate.Library.PolicyJson))
                continue;

            var result = await replacement.ReplaceMediaAsync(candidate.Id, cancellationToken);
            if (result.Replaced)
                logger.LogInformation("Auto-replaced original for media {MediaId}: {Path}", candidate.Id, result.OriginalPath);
            else
                logger.LogInformation("Auto-replace did not replace media {MediaId}: {Message}", candidate.Id, result.Message);
        }
    }

    private static bool LibraryAllowsReplace(string policyJson)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<LibraryPolicyDto>(policyJson, JsonOptions) ?? new LibraryPolicyDto();
            return policy.Output.ReplaceOriginals;
        }
        catch { return false; }
    }
}
