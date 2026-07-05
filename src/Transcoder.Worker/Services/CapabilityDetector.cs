using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class CapabilityDetector(IOptions<WorkerOptions> options, ILogger<CapabilityDetector> logger)
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<WorkerCapabilitiesDto> DetectAsync(CancellationToken cancellationToken = default)
    {
        var ffmpeg = await DetectToolAsync("ffmpeg", _options.FfmpegPath, "-version", cancellationToken);
        var ffprobe = await DetectToolAsync("ffprobe", _options.FfprobePath, "-version", cancellationToken);

        var encoders = ffmpeg.Available ? await ReadCodecsAsync(_options.FfmpegPath, "-hide_banner -encoders", cancellationToken) : [];

        var capabilities = new WorkerCapabilitiesDto
        {
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            FfmpegVersion = ffmpeg.VersionText,
            FfprobeVersion = ffprobe.VersionText,
            Encoders = encoders,
            Decoders = ffmpeg.Available ? await ReadCodecsAsync(_options.FfmpegPath, "-hide_banner -decoders", cancellationToken) : [],
            HwAccels = ffmpeg.Available ? await ReadHwaccelsAsync(cancellationToken) : [],
            AllowCpuEncoding = _options.Transcoding.AllowCpuEncoding,
            AllowGpuEncoding = _options.Transcoding.AllowGpuEncoding,
            CpuEncoders = _options.Transcoding.AllowCpuEncoding ? FilterCpuEncoders(encoders) : [],
            GpuEncoders = _options.Transcoding.AllowGpuEncoding ? FilterGpuEncoders(encoders) : []
        };

        if (!_options.Transcoding.AllowCpuEncoding)
            capabilities.Warnings.Add("CPU encoding is disabled by worker configuration.");

        if (!_options.Transcoding.AllowGpuEncoding)
            capabilities.Warnings.Add("GPU encoding is disabled by worker configuration.");

        if (!ffmpeg.Available)
        {
            capabilities.Warnings.Add("FFmpeg was not detected. Transcode jobs cannot run on this worker.");
            logger.LogWarning("FFmpeg was not detected at {Path}. Install ffmpeg or set Transcoder:FfmpegPath / TRANSCODER__FFMPEGPATH.", _options.FfmpegPath);
        }

        if (!ffprobe.Available)
        {
            capabilities.Warnings.Add("FFprobe was not detected. Probe, plan-review, and validation jobs cannot run on this worker.");
            logger.LogWarning("FFprobe was not detected at {Path}. Install ffprobe or set Transcoder:FfprobePath / TRANSCODER__FFPROBEPATH.", _options.FfprobePath);
        }

        return capabilities;
    }

    public IReadOnlyList<string> ValidateLocalRequirements(WorkerCapabilitiesDto capabilities)
    {
        var errors = new List<string>();

        if (_options.Roles.HasFlag(WorkerRole.Prober) && !capabilities.Ffprobe.Available)
            errors.Add("Worker role Prober requires ffprobe, but ffprobe is not available.");

        if (_options.Roles.HasFlag(WorkerRole.Validator) && !capabilities.Ffprobe.Available)
            errors.Add("Worker role Validator requires ffprobe, but ffprobe is not available.");

        if (_options.Roles.HasFlag(WorkerRole.Transcoder) && !capabilities.Ffmpeg.Available)
            errors.Add("Worker role Transcoder requires ffmpeg, but ffmpeg is not available.");

        return errors;
    }

    private async Task<ToolCapabilityDto> DetectToolAsync(string name, string fileName, string arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(fileName, arguments, cancellationToken);
        if (!result.Started)
        {
            return new ToolCapabilityDto
            {
                Name = name,
                Path = fileName,
                Available = false,
                Error = result.Error
            };
        }

        var firstLine = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return new ToolCapabilityDto
        {
            Name = name,
            Path = fileName,
            Available = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine),
            VersionText = firstLine,
            Version = ExtractVersion(firstLine),
            Error = result.ExitCode == 0 ? null : $"Exited with code {result.ExitCode}: {result.Output}".Trim()
        };
    }

    private static string? ExtractVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return null;
        var match = Regex.Match(versionText, @"\bversion\s+(?<version>\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : null;
    }


    private List<string> FilterCpuEncoders(IEnumerable<string> detectedEncoders)
    {
        var configured = new HashSet<string>(_options.Transcoding.CpuEncoders, StringComparer.OrdinalIgnoreCase);
        return detectedEncoders
            .Where(x => configured.Contains(x) || IsCpuEncoderName(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    private List<string> FilterGpuEncoders(IEnumerable<string> detectedEncoders)
    {
        var configured = new HashSet<string>(_options.Transcoding.GpuEncoders, StringComparer.OrdinalIgnoreCase);
        return detectedEncoders
            .Where(x => configured.Contains(x) || IsGpuEncoderName(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    private static bool IsGpuEncoderName(string? encoder)
    {
        var value = (encoder ?? string.Empty).Trim().ToLowerInvariant();
        return value.EndsWith("_nvenc")
            || value.EndsWith("_qsv")
            || value.EndsWith("_vaapi")
            || value.EndsWith("_amf")
            || value.EndsWith("_videotoolbox")
            || value.EndsWith("_v4l2m2m");
    }

    private static bool IsCpuEncoderName(string? encoder)
    {
        var value = (encoder ?? string.Empty).Trim().ToLowerInvariant();
        return value is "libx264" or "libx265" or "libsvtav1" or "libaom-av1" or "libvpx-vp9";
    }

    private async Task<List<string>> ReadCodecsAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(fileName, arguments, cancellationToken);
        if (!result.Started || result.ExitCode != 0) return [];

        var output = result.Output;
        var codecs = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*[A-Z\.]{6}\s+(?<name>\S+)");
            if (match.Success) codecs.Add(match.Groups["name"].Value);
        }
        return codecs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    }

    private async Task<List<string>> ReadHwaccelsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(_options.FfmpegPath, "-hide_banner -hwaccels", cancellationToken);
        if (!result.Started || result.ExitCode != 0) return [];

        return result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.Contains("Hardware acceleration methods", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null) return new CommandResult(false, -1, string.Empty, "Process.Start returned null.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var combined = string.IsNullOrWhiteSpace(output) ? error : output + (string.IsNullOrWhiteSpace(error) ? string.Empty : Environment.NewLine + error);
            return new CommandResult(true, process.ExitCode, combined, null);
        }
        catch (Win32Exception ex)
        {
            logger.LogDebug(ex, "Failed to start {FileName} {Arguments}", fileName, arguments);
            return new CommandResult(false, -1, string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run {FileName} {Arguments}", fileName, arguments);
            return new CommandResult(false, -1, string.Empty, ex.Message);
        }
    }

    private sealed record CommandResult(bool Started, int ExitCode, string Output, string? Error);
}
