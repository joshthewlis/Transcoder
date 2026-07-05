using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class FfmpegRunner(IOptions<WorkerOptions> options, ILogger<FfmpegRunner> logger)
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<FfmpegRunResult> RunAsync(
        IReadOnlyList<string> plannedArgs,
        string inputPath,
        string outputPath,
        Action<double?, string>? progress = null,
        CancellationToken cancellationToken = default)
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

        var log = new StringBuilder();
        var started = DateTime.UtcNow;
        progress?.Invoke(1, "Starting ffmpeg");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
            var message = BuildProgressMessage(e.Data);
            if (!string.IsNullOrWhiteSpace(message))
                progress?.Invoke(null, message);
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

        var elapsed = DateTime.UtcNow - started;
        var logText = log.ToString();

        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg failed with exit code {ExitCode}: {Log}", process.ExitCode, Tail(logText, 4000));
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}: {Tail(logText, 4000)}");
        }

        progress?.Invoke(95, "ffmpeg complete");
        return new FfmpegRunResult(process.ExitCode, elapsed, logText);
    }

    private static string? BuildProgressMessage(string line)
    {
        if (line.Contains("frame=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("time=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("speed=", StringComparison.OrdinalIgnoreCase))
        {
            return line.Trim();
        }

        return null;
    }

    private static string Tail(string value, int length)
    {
        if (value.Length <= length) return value;
        return value[^length..];
    }
}

public sealed record FfmpegRunResult(int ExitCode, TimeSpan Elapsed, string LogText);
