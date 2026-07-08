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

        foreach (var arg in plannedArgs)
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
            var update = BuildProgressUpdate(e.Data, inputDurationSeconds);
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

        if (process.ExitCode != 0)
        {
            var failureLog = Tail(logText, MaxFailureLogCharacters);
            logger.LogWarning("ffmpeg failed with exit code {ExitCode}: {Log}", process.ExitCode, failureLog);
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}: {failureLog}");
        }

        progress?.Invoke(95, "ffmpeg complete");
        return new FfmpegRunResult(process.ExitCode, elapsed, logText);
    }

    private static FfmpegProgressUpdate? BuildProgressUpdate(string line, double? inputDurationSeconds)
    {
        if (!line.Contains("frame=", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("time=", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("speed=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var message = line.Trim();
        var progress = TryCalculatePercentFromTimestamp(message, inputDurationSeconds);
        return new FfmpegProgressUpdate(progress, message);
    }

    private static double? TryCalculatePercentFromTimestamp(string line, double? inputDurationSeconds)
    {
        if (inputDurationSeconds is null || inputDurationSeconds.Value <= 0)
            return null;

        var match = Regex.Match(line, @"(?:^|\s)time=(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        if (!TimeSpan.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var current))
            return null;

        var percent = current.TotalSeconds / inputDurationSeconds.Value * 100d;
        return Math.Clamp(percent, 1d, 94d);
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

public sealed record FfmpegRunResult(int ExitCode, TimeSpan Elapsed, string LogText);
