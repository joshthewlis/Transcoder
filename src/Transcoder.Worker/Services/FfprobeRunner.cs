using System.Diagnostics;
using Microsoft.Extensions.Options;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class FfprobeRunner(IOptions<WorkerOptions> options, ILogger<FfprobeRunner> logger)
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<string> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            Arguments = $"-v error -print_format json -show_format -show_streams \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        if (process is null)
            throw new InvalidOperationException($"Failed to start ffprobe at '{_options.FfprobePath}'.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffprobe failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"ffprobe failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }
}
