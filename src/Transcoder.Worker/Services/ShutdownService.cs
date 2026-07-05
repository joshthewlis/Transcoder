using System.Diagnostics;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class ShutdownService(IOptions<WorkerOptions> options, IHostApplicationLifetime lifetime, ILogger<ShutdownService> logger)
{
    private readonly ShutdownOptions _options = options.Value.Shutdown;

    public async Task ApplyAsync(WorkerControlState state, CancellationToken cancellationToken)
    {
        switch (state)
        {
            case WorkerControlState.DrainThenExit when _options.AllowRemoteExitRequest:
                logger.LogInformation("Remote drain-then-exit requested; stopping worker process.");
                lifetime.StopApplication();
                break;

            case WorkerControlState.DrainThenShutdown when _options.AllowRemoteShutdownRequest:
                if (string.IsNullOrWhiteSpace(_options.ShutdownCommand))
                {
                    logger.LogWarning("Remote shutdown requested, but no local shutdown command is configured.");
                    return;
                }

                logger.LogInformation("Remote drain-then-shutdown requested; running configured shutdown command.");
                await RunShellCommandAsync(_options.ShutdownCommand, cancellationToken);
                break;

            default:
                logger.LogInformation("Control state {ControlState} does not permit local exit/shutdown.", state);
                break;
        }
    }

    private static async Task RunShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            UseShellExecute = false
        });
        if (process is not null)
            await process.WaitForExitAsync(cancellationToken);
    }
}
