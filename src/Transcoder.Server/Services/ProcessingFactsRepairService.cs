using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;

namespace Transcoder.Server.Services;

/// <summary>
/// Repairs completed cleanup/transcode facts from the immutable completed-job results.
/// This is primarily an upgrade path for older Reset/Replan behaviour that cleared MetadataJson.
/// It never changes plans, job state, files, or replacement state.
/// </summary>
public sealed class ProcessingFactsRepairService(
    TranscoderDbContext db,
    ILogger<ProcessingFactsRepairService> logger)
{
    public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
    {
        var completedWork = await db.Jobs.AsNoTracking()
            .Where(x => x.MediaItemId != null
                && x.Status == JobStatus.Completed
                && (x.JobType == JobType.Cleanup || x.JobType == JobType.Transcode)
                && x.ResultJson != null
                && x.ResultJson != "")
            .OrderBy(x => x.CompletedUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (completedWork.Count == 0)
            return 0;

        var mediaIds = completedWork
            .Select(x => x.MediaItemId!.Value)
            .Distinct()
            .ToList();

        var mediaById = await db.MediaItems
            .Where(x => mediaIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var repaired = 0;
        foreach (var group in completedWork.GroupBy(x => x.MediaItemId!.Value))
        {
            if (!mediaById.TryGetValue(group.Key, out var media))
                continue;

            var stats = MediaProcessingStatsStore.Read(media);
            stats.History ??= [];

            var changed = false;
            var parsed = group
                .Select(job => new { Job = job, Result = TryReadResult(job.ResultJson) })
                .Where(x => x.Result is not null)
                .ToList();

            if (parsed.Count == 0)
                continue;

            var earliestInput = parsed
                .Select(x => x.Result!.InputSizeBytes)
                .FirstOrDefault(x => x is > 0);

            if (stats.OriginalSizeBytes is null && earliestInput is > 0)
            {
                stats.OriginalSizeBytes = earliestInput;
                changed = true;
            }

            if (stats.CleanupSavedBytes is null)
            {
                var cleanup = parsed
                    .Where(x => x.Job.JobType == JobType.Cleanup && x.Result!.SavedBytes is not null)
                    .OrderByDescending(x => x.Job.CompletedUtc)
                    .ThenByDescending(x => x.Job.Id)
                    .FirstOrDefault();

                if (cleanup is not null)
                {
                    stats.CleanupSavedBytes = cleanup.Result!.SavedBytes;
                    changed = true;
                    AddRecoveredHistory(stats, cleanup.Job, cleanup.Result);
                }
            }

            if (stats.TranscodeSavedBytes is null)
            {
                var transcode = parsed
                    .Where(x => x.Job.JobType == JobType.Transcode && x.Result!.SavedBytes is not null)
                    .OrderByDescending(x => x.Job.CompletedUtc)
                    .ThenByDescending(x => x.Job.Id)
                    .FirstOrDefault();

                if (transcode is not null)
                {
                    stats.TranscodeSavedBytes = transcode.Result!.SavedBytes;
                    changed = true;
                    AddRecoveredHistory(stats, transcode.Job, transcode.Result);
                }
            }

            if (!changed)
                continue;

            // Do not infer ReplacedOriginal here. Old reset behaviour could erase that flag and
            // job results alone cannot prove whether a staged output was actually swapped in.
            MediaProcessingStatsStore.Write(media, stats);
            media.UpdatedUtc = DateTime.UtcNow;
            repaired++;
        }

        if (repaired > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Recovered completed cleanup/transcode savings for {Count} media item(s) from completed job results. " +
                "This repairs facts erased by older Reset/Replan behaviour.",
                repaired);
        }

        return repaired;
    }

    private static void AddRecoveredHistory(
        MediaProcessingStats stats,
        Transcoder.Server.Data.Entities.JobEntity job,
        RecoveredResult result)
    {
        var alreadyRecorded = stats.History.Any(x =>
            x.JobType == job.JobType
            && x.CompletedUtc == (job.CompletedUtc ?? job.CreatedUtc)
            && x.SavedBytes == result.SavedBytes);

        if (alreadyRecorded)
            return;

        MediaProcessingStatsStore.AddHistory(stats, new ProcessingHistoryEntry
        {
            Stage = job.JobType == JobType.Cleanup ? "CleanupRecovered" : "TranscodeRecovered",
            JobType = job.JobType,
            CompletedUtc = job.CompletedUtc ?? job.CreatedUtc,
            BeforeSizeBytes = result.InputSizeBytes,
            AfterSizeBytes = result.OutputSizeBytes,
            SavedBytes = result.SavedBytes,
            InputPath = result.InputPath,
            OutputPath = result.OutputPath,
            Message = "Recovered from completed job result after upgrade from destructive Reset/Replan behaviour."
        });
    }

    private static RecoveredResult? TryReadResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            var input = TryGetLong(root, "inputFileSizeBytes");
            var output = TryGetLong(root, "outputFileSizeBytes");
            var saved = input is not null && output is not null
                ? Math.Max(0, input.Value - output.Value)
                : (long?)null;

            return new RecoveredResult(
                input,
                output,
                saved,
                TryGetString(root, "inputPath"),
                TryGetString(root, "stagingOutputPath"));
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private sealed record RecoveredResult(
        long? InputSizeBytes,
        long? OutputSizeBytes,
        long? SavedBytes,
        string? InputPath,
        string? OutputPath);
}
