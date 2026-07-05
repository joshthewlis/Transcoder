using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Services;

public static class ProfileSeeder
{
    public static async Task SeedDefaultsAsync(TranscoderDbContext db, CancellationToken cancellationToken = default)
    {
        var defaults = new[]
        {
            Create("hevc_nvenc_balanced", "hevc", "hevc_nvenc", ["-c:v", "hevc_nvenc", "-preset", "p5", "-cq", "23"], ["hevc_nvenc"]),
            Create("hevc_qsv_balanced", "hevc", "hevc_qsv", ["-c:v", "hevc_qsv", "-global_quality", "23"], ["hevc_qsv"]),
            Create("hevc_vaapi_balanced", "hevc", "hevc_vaapi", ["-c:v", "hevc_vaapi", "-qp", "24"], ["hevc_vaapi"]),
            Create("hevc_amf_balanced", "hevc", "hevc_amf", ["-c:v", "hevc_amf", "-quality", "balanced", "-qp_i", "23", "-qp_p", "23"], ["hevc_amf"]),
            Create("libx265_quality", "hevc", "libx265", ["-c:v", "libx265", "-preset", "medium", "-crf", "23"], ["libx265"]),
            Create("av1_qsv_balanced", "av1", "av1_qsv", ["-c:v", "av1_qsv", "-global_quality", "28"], ["av1_qsv"]),
            Create("av1_nvenc_balanced", "av1", "av1_nvenc", ["-c:v", "av1_nvenc", "-preset", "p5", "-cq", "30"], ["av1_nvenc"]),
            Create("libsvtav1_quality", "av1", "libsvtav1", ["-c:v", "libsvtav1", "-preset", "6", "-crf", "30"], ["libsvtav1"]),
            Create("copy_video_cleanup_streams", "copy", "copy", ["-c:v", "copy"], [])
        };

        var existingNames = await db.Profiles.Select(x => x.Name).ToListAsync(cancellationToken);
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var missing = defaults.Where(x => !existing.Contains(x.Name)).ToList();
        if (missing.Count == 0)
            return;

        db.Profiles.AddRange(missing);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ProfileEntity Create(string name, string targetCodec, string encoder, List<string> arguments, List<string> requiredEncoders) => new()
    {
        Name = name,
        TargetCodec = targetCodec,
        Encoder = encoder,
        ArgumentsJson = JsonSerializer.Serialize(arguments),
        RequiredEncodersJson = JsonSerializer.Serialize(requiredEncoders),
        Enabled = true
    };
}
