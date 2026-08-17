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
            .CountAsync(x => x.Status == MediaStatus.StagedCleaned || x.Status == MediaStatus.Staged || x.Status == MediaStatus.Approved, cancellationToken);
        var replaceFailures = await db.MediaItems.AsNoTracking()
            .CountAsync(x => x.Status == MediaStatus.ReplaceFailed, cancellationToken);
        var policyJson = await db.Libraries.AsNoTracking().Where(x => x.Enabled).Select(x => x.PolicyJson).ToListAsync(cancellationToken);

        return new ServerActivityDto
        {
            Busy = snapshot.Current is not null,
            SafeToStop = snapshot.Current is null,
            ProcessingMode = mode,
            AutoReplaceEnabled = mode == ProcessingMode.ReplaceApproved,
            ActiveHoursAllowStagedWork = activeHours.AllowStagedWork,
            ActiveHoursMessage = activeHours.Message,
            ReplaceEnabledLibraries = policyJson.Count(LibraryAllowsReplace),
            StagedWaiting = stagedWaiting,
            ReplaceFailures = replaceFailures,
            Current = ToDto(snapshot.Current),
            Last = ToDto(snapshot.Last)
        };
    }

    private static bool LibraryAllowsReplace(string policyJson)
    {
        try { return JsonSerializer.Deserialize<LibraryPolicyDto>(policyJson, JsonOptions)?.Output?.ReplaceOriginals == true; }
        catch { return false; }
    }

    private static ServerActivityOperationDto? ToDto(ServerActivityOperation? op)
    {
        if (op is null) return null;
        var now = op.CompletedUtc ?? DateTime.UtcNow;
        var elapsed = Math.Max(0, (now - op.StartedUtc).TotalSeconds);
        var stageElapsed = Math.Max(0.001, (now - op.StageStartedUtc).TotalSeconds);
        double? progress = null, speed = null, eta = null;

        if (op.BytesProcessed is >= 0 && op.TotalBytes is > 0)
        {
            progress = Math.Clamp(op.BytesProcessed.Value * 100d / op.TotalBytes.Value, 0, 100);
            if (op.BytesProcessed.Value > 0)
            {
                speed = op.BytesProcessed.Value / stageElapsed;
                if (speed > 0)
                    eta = Math.Max(0, (op.TotalBytes.Value - op.BytesProcessed.Value) / speed.Value);
            }
        }

        return new ServerActivityOperationDto
        {
            Operation = op.Operation, MediaId = op.MediaId, LibraryId = op.LibraryId, LibraryName = op.LibraryName,
            RelativePath = op.RelativePath, OriginalPath = op.OriginalPath, StagingPath = op.StagingPath,
            Stage = op.Stage, Message = op.Message, BytesProcessed = op.BytesProcessed, TotalBytes = op.TotalBytes,
            ProgressPercent = progress, BytesPerSecond = speed, EtaSeconds = eta, StartedUtc = op.StartedUtc,
            StageStartedUtc = op.StageStartedUtc, UpdatedUtc = op.UpdatedUtc, CompletedUtc = op.CompletedUtc,
            Success = op.Success, ElapsedSeconds = elapsed
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
    public double? ProgressPercent { get; set; }
    public double? BytesPerSecond { get; set; }
    public double? EtaSeconds { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime StageStartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool? Success { get; set; }
    public double ElapsedSeconds { get; set; }
}
