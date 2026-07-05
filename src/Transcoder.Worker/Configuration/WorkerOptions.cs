using Transcoder.Contracts;

namespace Transcoder.Worker.Configuration;

public sealed class WorkerOptions
{
    public string ServerUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "dev-worker-key";
    public string WorkerId { get; set; } = Environment.MachineName.ToLowerInvariant();
    public string WorkerName { get; set; } = Environment.MachineName;
    public WorkerRole Roles { get; set; } = WorkerRole.Prober;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public bool StopIfRequiredToolsMissing { get; set; } = true;
    public string LocalWorkingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "transcoder-worker");
    public bool AutoCreateLocalWorkingRoot { get; set; } = true;
    public bool KeepLocalJobFilesOnSuccess { get; set; }
    public TranscodeWorkingMode TranscodeWorkingMode { get; set; } = TranscodeWorkingMode.LocalThenCopyToStaging;
    public WorkerTranscodingOptions Transcoding { get; set; } = new();
    public WorkerLimitsDto Limits { get; set; } = new();
    public List<PathMappingOptions> PathMappings { get; set; } = [];
    public ShutdownOptions Shutdown { get; set; } = new();
}

public sealed class WorkerTranscodingOptions
{
    public bool AllowCpuEncoding { get; set; } = true;
    public bool AllowGpuEncoding { get; set; } = true;
    public List<string> CpuEncoders { get; set; } = ["libx265", "libx264", "libsvtav1", "libaom-av1"];
    public List<string> GpuEncoders { get; set; } = ["hevc_nvenc", "h264_nvenc", "av1_nvenc", "hevc_qsv", "h264_qsv", "av1_qsv", "hevc_vaapi", "h264_vaapi", "av1_vaapi", "hevc_amf", "h264_amf", "av1_amf", "hevc_videotoolbox", "h264_videotoolbox"];
}

public sealed class PathMappingOptions
{
    public string ServerPrefix { get; set; } = string.Empty;
    public string WorkerPrefix { get; set; } = string.Empty;
}

public sealed class ShutdownOptions
{
    public bool AllowRemoteShutdownRequest { get; set; }
    public bool AllowRemoteExitRequest { get; set; } = true;
    public string? ShutdownCommand { get; set; }
    public int IdleSecondsBeforeAction { get; set; } = 30;
}
