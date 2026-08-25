using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class FfmpegRunner(IOptions<WorkerOptions> options, ILogger<FfmpegRunner> logger)
{
    private const int MaxStoredLogCharacters = 256 * 1024;
    private const int MaxFailureLogCharacters = 12 * 1024;
    private readonly WorkerOptions _options = options.Value;

    public async Task<FfmpegRunResult> RunAsync(
        IReadOnlyList<string> plannedArgs,
        string inputPath,
        string outputPath,
        Action<double?, string>? progress = null,
        CancellationToken cancellationToken = default,
        double? inputDurationSeconds = null)
    {
        if (plannedArgs.Count == 0)
            throw new InvalidOperationException("FFmpeg plan contains no arguments.");

        var currentArgs = plannedArgs.ToList();
        var combinedLog = new StringBuilder();
        var totalElapsed = TimeSpan.Zero;

        var run = await RunProcessAsync(currentArgs, inputPath, outputPath, progress, cancellationToken, inputDurationSeconds);
        totalElapsed += run.Elapsed;
        combinedLog.Append(run.LogText);

        if (run.ExitCode == 0)
            return run;

        var failureLog = Tail(run.LogText, MaxFailureLogCharacters);

        if (TryBuildUnsupportedSubtitleRetryArgs(currentArgs, failureLog, out var subtitleRetryArgs, out var omittedSubtitleIndexes))
        {
            logger.LogWarning(
                "ffmpeg failed because mapped subtitle streams are unsupported/unknown. Retrying once without source subtitle stream(s) [{StreamIndexes}]. Video and audio mappings are unchanged.",
                string.Join(",", omittedSubtitleIndexes));

            progress?.Invoke(1, $"Retrying ffmpeg without unsupported subtitle stream(s) {string.Join(",", omittedSubtitleIndexes)}");
            TryDeleteOutput(outputPath);

            run = await RunProcessAsync(subtitleRetryArgs, inputPath, outputPath, progress, cancellationToken, inputDurationSeconds);
            totalElapsed += run.Elapsed;
            combinedLog.AppendLine();
            combinedLog.AppendLine($"--- Transcoder compatibility retry: omitted unsupported subtitle stream(s) {string.Join(",", omittedSubtitleIndexes)} ---");
            combinedLog.Append(run.LogText);

            if (run.ExitCode == 0)
            {
                logger.LogWarning(
                    "ffmpeg compatibility retry succeeded after omitting unsupported subtitle stream(s) [{StreamIndexes}].",
                    string.Join(",", omittedSubtitleIndexes));
                return new FfmpegRunResult(0, totalElapsed, combinedLog.ToString());
            }

            currentArgs = subtitleRetryArgs;
            failureLog = Tail(run.LogText, MaxFailureLogCharacters);
        }

        if (TryBuildM4vHevcMuxerRetryArgs(currentArgs, outputPath, failureLog, out var m4vRetryArgs))
        {
            logger.LogWarning(
                "ffmpeg selected the M4V/iPod muxer for {OutputPath}, which rejected HEVC. Retrying once with the MP4 muxer while retaining the .m4v filename.",
                outputPath);

            progress?.Invoke(1, "Retrying HEVC .m4v output using MP4 muxer");
            TryDeleteOutput(outputPath);

            run = await RunProcessAsync(m4vRetryArgs, inputPath, outputPath, progress, cancellationToken, inputDurationSeconds);
            totalElapsed += run.Elapsed;
            combinedLog.AppendLine();
            combinedLog.AppendLine("--- Transcoder compatibility retry: forced MP4 muxer for HEVC .m4v output ---");
            combinedLog.Append(run.LogText);

            if (run.ExitCode == 0)
            {
                logger.LogWarning(
                    "ffmpeg compatibility retry succeeded using MP4 muxer for HEVC .m4v output {OutputPath}.",
                    outputPath);
                return new FfmpegRunResult(0, totalElapsed, combinedLog.ToString());
            }

            failureLog = Tail(run.LogText, MaxFailureLogCharacters);
        }

        logger.LogWarning("ffmpeg failed with exit code {ExitCode}: {Log}", run.ExitCode, failureLog);
        throw CreateFfmpegException(run.ExitCode, failureLog);
    }

    private async Task<FfmpegRunResult> RunProcessAsync(
        IReadOnlyList<string> args,
        string inputPath,
        string outputPath,
        Action<double?, string>? progress,
        CancellationToken cancellationToken,
        double? inputDurationSeconds)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg switch
            {
                "{input}" => inputPath,
                "{output}" => outputPath,
                _ => arg
            });
        }

        var log = new BoundedLogBuffer(MaxStoredLogCharacters);
        var started = DateTime.UtcNow;
        progress?.Invoke(1, "Starting ffmpeg");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
            var update = BuildProgressUpdate(e.Data, inputDurationSeconds, DateTime.UtcNow - started);
            if (update is not null)
                progress?.Invoke(update.Progress, update.Message);
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start ffmpeg at '{_options.FfmpegPath}'.");

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();

        var elapsed = DateTime.UtcNow - started;
        var logText = log.ToString();

        if (process.ExitCode == 0)
            progress?.Invoke(95, "ffmpeg complete");

        return new FfmpegRunResult(process.ExitCode, elapsed, logText);
    }

    private static FfmpegException CreateFfmpegException(int exitCode, string failureLog)
    {
        var errorCode = IsInvalidStreamMapFailure(failureLog)
            ? FfmpegErrorCodes.InvalidStreamMap
            : FfmpegErrorCodes.FfmpegFailed;
        return new FfmpegException(errorCode, exitCode, failureLog);
    }

    private static bool TryBuildUnsupportedSubtitleRetryArgs(
        IReadOnlyList<string> currentArgs,
        string failureLog,
        out List<string> retryArgs,
        out List<int> omittedSubtitleIndexes)
    {
        retryArgs = [];
        omittedSubtitleIndexes = [];

        if (!LooksLikeUnsupportedSubtitleFailure(failureLog))
            return false;

        var indexes = new HashSet<int>();

        foreach (Match match in Regex.Matches(
                     failureLog,
                     @"Could not find codec parameters for stream\s+(?<index>\d+)\s+\(Subtitle:\s*none\)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                indexes.Add(index);
        }

        foreach (Match match in Regex.Matches(
                     failureLog,
                     @"Stream\s+#0:(?<index>\d+)(?:\([^)]+\))?:\s*Subtitle:\s*none",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                indexes.Add(index);
        }

        if (indexes.Count == 0)
            return false;

        var removed = new HashSet<int>();
        for (var i = 0; i < currentArgs.Count; i++)
        {
            if (currentArgs[i].Equals("-map", StringComparison.OrdinalIgnoreCase)
                && i + 1 < currentArgs.Count
                && TryParseExplicitInputStreamMap(currentArgs[i + 1], out var streamIndex)
                && indexes.Contains(streamIndex))
            {
                removed.Add(streamIndex);
                i++;
                continue;
            }
            retryArgs.Add(currentArgs[i]);
        }

        if (removed.Count == 0)
            return false;

        omittedSubtitleIndexes = removed.OrderBy(x => x).ToList();
        return true;
    }

    private static bool TryBuildM4vHevcMuxerRetryArgs(
        IReadOnlyList<string> currentArgs,
        string outputPath,
        string failureLog,
        out List<string> retryArgs)
    {
        retryArgs = [];

        if (!Path.GetExtension(outputPath).Equals(".m4v", StringComparison.OrdinalIgnoreCase))
            return false;

        var muxerRejectedHevc =
            failureLog.Contains("Could not find tag for codec hevc", StringComparison.OrdinalIgnoreCase)
            && failureLog.Contains("codec not currently supported in container", StringComparison.OrdinalIgnoreCase)
            && failureLog.Contains("Could not write header", StringComparison.OrdinalIgnoreCase);

        if (!muxerRejectedHevc)
            return false;

        retryArgs = currentArgs.ToList();
        var outputIndex = retryArgs.FindLastIndex(x => x.Equals("{output}", StringComparison.Ordinal));
        if (outputIndex < 0)
            return false;

        if (retryArgs.Take(outputIndex).Any(x => x.Equals("-f", StringComparison.OrdinalIgnoreCase)))
            return false;

        retryArgs.Insert(outputIndex, "mp4");
        retryArgs.Insert(outputIndex, "-f");
        return true;
    }

    private static bool LooksLikeUnsupportedSubtitleFailure(string logText)
    {
        var hasUnsupportedCodec =
            logText.Contains("Unknown/unsupported AVCodecID", StringComparison.OrdinalIgnoreCase)
            || logText.Contains("Subtitle codec 0 is not supported", StringComparison.OrdinalIgnoreCase)
            || logText.Contains("Subtitle: none", StringComparison.OrdinalIgnoreCase);

        var headerFailed =
            logText.Contains("Could not write header", StringComparison.OrdinalIgnoreCase)
            || logText.Contains("Error opening output", StringComparison.OrdinalIgnoreCase);

        return hasUnsupportedCodec && headerFailed;
    }

    private static bool TryParseExplicitInputStreamMap(string value, out int streamIndex)
    {
        streamIndex = -1;
        var match = Regex.Match(value.Trim(), @"^0:(?<index>\d+)$", RegexOptions.CultureInvariant);
        return match.Success
            && int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out streamIndex);
    }

    private static void TryDeleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool IsInvalidStreamMapFailure(string logText)
    {
        return logText.Contains("Stream map", StringComparison.OrdinalIgnoreCase) &&
               logText.Contains("matches no streams", StringComparison.OrdinalIgnoreCase);
    }

    private static FfmpegProgressUpdate? BuildProgressUpdate(string line, double? inputDurationSeconds, TimeSpan elapsed)
    {
        if (!line.Contains("frame=", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("time=", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("speed=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var message = line.Trim();
        var currentSeconds = TryParseFfmpegTimestampSeconds(message);
        var progress = TryCalculatePercentFromTimestamp(currentSeconds, inputDurationSeconds);
        var eta = TryCalculateEta(currentSeconds, inputDurationSeconds, TryParseFfmpegSpeed(message), elapsed);

        if (eta is not null)
            message = $"{message} eta={FormatDuration(eta.Value)}";

        return new FfmpegProgressUpdate(progress, message);
    }

    private static double? TryCalculatePercentFromTimestamp(double? currentSeconds, double? inputDurationSeconds)
    {
        if (currentSeconds is null || currentSeconds.Value <= 0 || inputDurationSeconds is null || inputDurationSeconds.Value <= 0)
            return null;

        var percent = currentSeconds.Value / inputDurationSeconds.Value * 100d;
        return Math.Clamp(percent, 1d, 94d);
    }

    private static double? TryParseFfmpegTimestampSeconds(string line)
    {
        var match = Regex.Match(line, @"(?:^|\s)time=(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        return TimeSpan.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var current)
            ? current.TotalSeconds
            : null;
    }

    private static double? TryParseFfmpegSpeed(string line)
    {
        var match = Regex.Match(line, @"(?:^|\s)speed=\s*(?<speed>[0-9]+(?:\.[0-9]+)?)x", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        if (!double.TryParse(match.Groups["speed"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            return null;

        return speed > 0 ? speed : null;
    }

    private static TimeSpan? TryCalculateEta(double? currentSeconds, double? inputDurationSeconds, double? ffmpegSpeed, TimeSpan elapsed)
    {
        if (currentSeconds is null || currentSeconds.Value <= 0 || inputDurationSeconds is null || inputDurationSeconds.Value <= 0)
            return null;

        var remainingMediaSeconds = inputDurationSeconds.Value - currentSeconds.Value;
        if (remainingMediaSeconds <= 0)
            return TimeSpan.Zero;

        var effectiveSpeed = ffmpegSpeed is > 0
            ? ffmpegSpeed.Value
            : elapsed.TotalSeconds > 0
                ? currentSeconds.Value / elapsed.TotalSeconds
                : 0;

        if (effectiveSpeed <= 0)
            return null;

        return TimeSpan.FromSeconds(remainingMediaSeconds / effectiveSpeed);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (duration.TotalHours >= 1)
            return $"~{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"~{duration.Minutes}m {duration.Seconds}s";

        return $"~{Math.Max(0, duration.Seconds)}s";
    }

    private static string Tail(string value, int length)
    {
        if (value.Length <= length) return value;
        return value[^length..];
    }

    private sealed record FfmpegProgressUpdate(double? Progress, string Message);

    private sealed class BoundedLogBuffer
    {
        private readonly int _maxCharacters;
        private readonly StringBuilder _builder = new();

        public BoundedLogBuffer(int maxCharacters)
        {
            _maxCharacters = Math.Max(1024, maxCharacters);
        }

        public void AppendLine(string line)
        {
            _builder.AppendLine(line);
            if (_builder.Length > _maxCharacters)
                _builder.Remove(0, _builder.Length - _maxCharacters);
        }

        public override string ToString() => _builder.ToString();
    }
}

public static class FfmpegErrorCodes
{
    public const string FfmpegFailed = "FfmpegFailed";
    public const string InvalidStreamMap = "InvalidStreamMap";
}

public sealed class FfmpegException : InvalidOperationException
{
    public FfmpegException(string errorCode, int exitCode, string ffmpegLog)
        : base($"ffmpeg failed with exit code {exitCode}: {ffmpegLog}")
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? FfmpegErrorCodes.FfmpegFailed : errorCode;
        ExitCode = exitCode;
        FfmpegLog = ffmpegLog;
    }

    public string ErrorCode { get; }
    public int ExitCode { get; }
    public string FfmpegLog { get; }
}

public sealed record FfmpegRunResult(int ExitCode, TimeSpan Elapsed, string LogText);
