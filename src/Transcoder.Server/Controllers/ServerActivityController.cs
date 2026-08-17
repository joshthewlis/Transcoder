using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/server/activity")]
public sealed class ServerActivityController(
    TranscoderDbContext db,
    ServerActivityState activity,
    SystemSettingsService settings) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpGet]
    public async Task<ActionResult<ServerActivityDto>> Get(CancellationToken cancellationToken)
    {
        var snapshot = activity.Snapshot();
        var mode = await settings.GetProcessingModeAsync(cancellationToken);
        var execution = await settings.GetExecutionSettingsAsync(cancellationToken);
        var activeHours = ActiveHoursEvaluator.Evaluate(execution, DateTime.UtcNow);

        var stagedWaiting = await db.MediaItems.AsNoTracking()
            .CountAsync(x =>
                x.Status == MediaStatus.StagedCleaned
                || x.Status == MediaStatus.Staged
                || x.Status == MediaStatus.Approved,
                cancellationToken);

        var replaceFailures = await db.MediaItems.AsNoTracking()
            .CountAsync(x => x.Status == MediaStatus.ReplaceFailed, cancellationToken);

        var enabledLibraryPolicies = await db.Libraries.AsNoTracking()
            .Where(x => x.Enabled)
            .Select(x => x.PolicyJson)
            .ToListAsync(cancellationToken);

        var replaceEnabledLibraries = enabledLibraryPolicies.Count(LibraryAllowsReplace);

        return new ServerActivityDto
        {
            Busy = snapshot.Current is not null,
            SafeToStop = snapshot.Current is null,
            ProcessingMode = mode,
            AutoReplaceEnabled = mode == ProcessingMode.ReplaceApproved,
            ActiveHoursAllowStagedWork = activeHours.AllowStagedWork,
            ActiveHoursMessage = activeHours.Message,
            ReplaceEnabledLibraries = replaceEnabledLibraries,
            StagedWaiting = stagedWaiting,
            ReplaceFailures = replaceFailures,
            Current = ToDto(snapshot.Current),
            Last = ToDto(snapshot.Last)
        };
    }

    private static bool LibraryAllowsReplace(string policyJson)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<LibraryPolicyDto>(policyJson, JsonOptions);
            return policy?.Output?.ReplaceOriginals == true;
        }
        catch
        {
            return false;
        }
    }

    private static ServerActivityOperationDto? ToDto(ServerActivityOperation? operation)
    {
        if (operation is null) return null;

        var elapsedEnd = operation.CompletedUtc ?? DateTime.UtcNow;
        return new ServerActivityOperationDto
        {
            Operation = operation.Operation,
            MediaId = operation.MediaId,
            LibraryId = operation.LibraryId,
            LibraryName = operation.LibraryName,
            RelativePath = operation.RelativePath,
            OriginalPath = operation.OriginalPath,
            StagingPath = operation.StagingPath,
            Stage = operation.Stage,
            Message = operation.Message,
            BytesProcessed = operation.BytesProcessed,
            TotalBytes = operation.TotalBytes,
            StartedUtc = operation.StartedUtc,
            UpdatedUtc = operation.UpdatedUtc,
            CompletedUtc = operation.CompletedUtc,
            Success = operation.Success,
            ElapsedSeconds = Math.Max(0, (elapsedEnd - operation.StartedUtc).TotalSeconds)
        };
    }
}

public sealed class ServerActivityDto
{
    public bool Busy { get; set; }
    public bool SafeToStop { get; set; }
    public ProcessingMode ProcessingMode { get; set; }
    public bool AutoReplaceEnabled { get; set; }
    public bool ActiveHoursAllowStagedWork { get; set; }
    public string? ActiveHoursMessage { get; set; }
    public int ReplaceEnabledLibraries { get; set; }
    public int StagedWaiting { get; set; }
    public int ReplaceFailures { get; set; }
    public ServerActivityOperationDto? Current { get; set; }
    public ServerActivityOperationDto? Last { get; set; }
}

public sealed class ServerActivityOperationDto
{
    public string Operation { get; set; } = string.Empty;
    public long? MediaId { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string? RelativePath { get; set; }
    public string? OriginalPath { get; set; }
    public string? StagingPath { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string? Message { get; set; }
    public long? BytesProcessed { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool? Success { get; set; }
    public double ElapsedSeconds { get; set; }
}
