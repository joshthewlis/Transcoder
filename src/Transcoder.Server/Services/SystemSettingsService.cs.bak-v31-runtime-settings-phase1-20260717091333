using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class SystemSettingsService(TranscoderDbContext db, IOptions<ActiveHoursOptions> activeHoursDefaults)
{
    private const string ProcessingModeKey = "processingMode";
    private const string AutoQueueCleanupJobsKey = "autoQueueCleanupJobs";
    private const string AutoQueueTranscodeJobsKey = "autoQueueTranscodeJobs";
    private const string RequirePlanReviewBeforeAutoQueueKey = "requirePlanReviewBeforeAutoQueue";
    private const string ActiveHoursEnabledKey = "activeHoursEnabled";
    private const string ActiveHoursTimeZoneIdKey = "activeHoursTimeZoneId";
    private const string ActiveHoursStartKey = "activeHoursStart";
    private const string ActiveHoursStopKey = "activeHoursStop";
    private const string StopNewWorkMinutesBeforeKey = "stopNewWorkMinutesBefore";

    public async Task<ProcessingMode> GetProcessingModeAsync(CancellationToken cancellationToken = default)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == ProcessingModeKey, cancellationToken);
        return Enum.TryParse<ProcessingMode>(setting?.Value, out var mode) ? mode : ProcessingMode.PlanAndReview;
    }

    public async Task SetProcessingModeAsync(ProcessingMode mode, CancellationToken cancellationToken = default)
    {
        await SetValueAsync(ProcessingModeKey, mode.ToString(), cancellationToken);
    }

    public async Task<ExecutionSettingsDto> GetExecutionSettingsAsync(CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AutoQueueCleanupJobsKey,
            AutoQueueTranscodeJobsKey,
            RequirePlanReviewBeforeAutoQueueKey,
            ActiveHoursEnabledKey,
            ActiveHoursTimeZoneIdKey,
            ActiveHoursStartKey,
            ActiveHoursStopKey,
            StopNewWorkMinutesBeforeKey
        };

        var values = await db.SystemSettings
            .Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);

        return new ExecutionSettingsDto
        {
            AutoQueueCleanupJobs = ReadBool(values, AutoQueueCleanupJobsKey, defaultValue: false),
            AutoQueueTranscodeJobs = ReadBool(values, AutoQueueTranscodeJobsKey, defaultValue: false),
            RequirePlanReviewBeforeAutoQueue = ReadBool(values, RequirePlanReviewBeforeAutoQueueKey, defaultValue: true),
            ActiveHoursEnabled = ReadBool(values, ActiveHoursEnabledKey, activeHoursDefaults.Value.Enabled),
            ActiveHoursTimeZoneId = ReadString(values, ActiveHoursTimeZoneIdKey, activeHoursDefaults.Value.TimeZoneId),
            ActiveHoursStart = ReadString(values, ActiveHoursStartKey, activeHoursDefaults.Value.Start),
            ActiveHoursStop = ReadString(values, ActiveHoursStopKey, activeHoursDefaults.Value.Stop),
            StopNewWorkMinutesBefore = ReadInt(values, StopNewWorkMinutesBeforeKey, activeHoursDefaults.Value.StopNewWorkMinutesBefore)
        };
    }

    public async Task SetExecutionSettingsAsync(ExecutionSettingsDto request, CancellationToken cancellationToken = default)
    {
        await SetValueAsync(AutoQueueCleanupJobsKey, request.AutoQueueCleanupJobs.ToString(), cancellationToken, saveChanges: false);
        await SetValueAsync(AutoQueueTranscodeJobsKey, request.AutoQueueTranscodeJobs.ToString(), cancellationToken, saveChanges: false);
        await SetValueAsync(RequirePlanReviewBeforeAutoQueueKey, request.RequirePlanReviewBeforeAutoQueue.ToString(), cancellationToken, saveChanges: false);
        await SetValueAsync(ActiveHoursEnabledKey, request.ActiveHoursEnabled.ToString(), cancellationToken, saveChanges: false);
        await SetValueAsync(ActiveHoursTimeZoneIdKey, NormalizeString(request.ActiveHoursTimeZoneId, activeHoursDefaults.Value.TimeZoneId), cancellationToken, saveChanges: false);
        await SetValueAsync(ActiveHoursStartKey, NormalizeString(request.ActiveHoursStart, activeHoursDefaults.Value.Start), cancellationToken, saveChanges: false);
        await SetValueAsync(ActiveHoursStopKey, NormalizeString(request.ActiveHoursStop, activeHoursDefaults.Value.Stop), cancellationToken, saveChanges: false);
        await SetValueAsync(StopNewWorkMinutesBeforeKey, Math.Clamp(request.StopNewWorkMinutesBefore, 0, 24 * 60 - 1).ToString(), cancellationToken, saveChanges: false);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkerRuntimeSettingsDto> GetWorkerRuntimeSettingsAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var key = $"worker:{workerId}:runtimeSettings";
        var setting = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
            return new WorkerRuntimeSettingsDto { UpdatedUtc = DateTime.MinValue };

        try
        {
            return JsonSerializer.Deserialize<WorkerRuntimeSettingsDto>(setting.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? new WorkerRuntimeSettingsDto { UpdatedUtc = DateTime.MinValue };
        }
        catch
        {
            return new WorkerRuntimeSettingsDto { UpdatedUtc = DateTime.MinValue };
        }
    }

    public async Task SetWorkerRuntimeSettingsAsync(string workerId, WorkerRuntimeSettingsDto settings, CancellationToken cancellationToken = default)
    {
        settings.UpdatedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });
        await SetValueAsync($"worker:{workerId}:runtimeSettings", json, cancellationToken);
    }

    private async Task SetValueAsync(string key, string value, CancellationToken cancellationToken, bool saveChanges = true)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSettingEntity { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        if (saveChanges)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var value)) return defaultValue;
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        if (!values.TryGetValue(key, out var value)) return defaultValue;
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key, string defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return defaultValue;
        return value.Trim();
    }

    private static string NormalizeString(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}
