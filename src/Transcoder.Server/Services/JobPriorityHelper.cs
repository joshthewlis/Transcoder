using System.Text.Json;
using System.Text.Json.Nodes;
using Transcoder.Contracts;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public static class JobPriorityHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static JobQueuePriority ReadPriority(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return JobQueuePriority.Normal;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.TryGetProperty("queuePriorityValue", out var numeric) && numeric.ValueKind == JsonValueKind.Number && numeric.TryGetInt32(out var value))
                return ClampPriority(value);

            if (root.TryGetProperty("queuePriority", out var text))
            {
                var raw = text.ValueKind == JsonValueKind.String ? text.GetString() : text.ToString();
                if (Enum.TryParse<JobQueuePriority>(raw, ignoreCase: true, out var parsed))
                    return parsed;
                if (int.TryParse(raw, out var parsedNumber))
                    return ClampPriority(parsedNumber);
            }
        }
        catch
        {
            // Legacy or malformed payload. Treat as normal priority.
        }

        return JobQueuePriority.Normal;
    }

    public static int ReadPriorityValue(string? payloadJson) => (int)ReadPriority(payloadJson);

    public static void SetPriority(JobEntity job, JobQueuePriority priority)
    {
        var payload = TryReadPayload(job.PayloadJson);
        payload["queuePriority"] = priority.ToString();
        payload["queuePriorityValue"] = (int)priority;
        job.PayloadJson = payload.ToJsonString(JsonOptions);

        var marker = $"Priority set to {priority}.";
        job.LastMessage = string.IsNullOrWhiteSpace(job.LastMessage)
            ? marker
            : job.LastMessage.Contains(marker, StringComparison.OrdinalIgnoreCase) ? job.LastMessage : $"{marker} {job.LastMessage}";

        if (job.Status == JobStatus.Queued && (int)priority > (int)JobQueuePriority.Normal)
        {
            // Keep the existing DB schema: high priority is also moved older in queue time,
            // so normal CreatedUtc-based leasing sees it before ordinary queued work.
            var offsetYears = (int)priority >= (int)JobQueuePriority.Urgent ? -10 : -5;
            job.CreatedUtc = DateTime.UtcNow.AddYears(offsetYears).AddMilliseconds(job.Id % 100000);
            job.QueuedUtc = job.CreatedUtc;
        }
    }

    private static JsonObject TryReadPayload(string? payloadJson)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                return JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
            }
            catch
            {
                // Fall through and start a clean object.
            }
        }

        return new JsonObject();
    }

    private static JobQueuePriority ClampPriority(int value)
    {
        if (value >= (int)JobQueuePriority.Urgent) return JobQueuePriority.Urgent;
        if (value >= (int)JobQueuePriority.High) return JobQueuePriority.High;
        if (value <= (int)JobQueuePriority.Low) return JobQueuePriority.Low;
        return JobQueuePriority.Normal;
    }
}
