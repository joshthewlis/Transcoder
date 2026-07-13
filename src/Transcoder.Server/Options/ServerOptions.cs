namespace Transcoder.Server.Options;

public sealed class TranscoderServerOptions
{
    public string ApiVersion { get; set; } = "1";
    public int PollSeconds { get; set; } = 10;
}

public sealed class SecurityOptions
{
    public string? AdminApiKey { get; set; }
    public string? WorkerApiKey { get; set; }
}

public sealed class WorkerTimingOptions
{
    public int ExpectedHeartbeatSeconds { get; set; } = 30;
    public int UnresponsiveAfterSeconds { get; set; } = 60;
    public int LostAfterSeconds { get; set; } = 300;
    public int MaxLeaseSeconds { get; set; } = 300;
}

public sealed class StorageOptions
{
    public string TranscoderRoot { get; set; } = "/mnt/.transcoder";
    public string WorkingRoot { get; set; } = "/mnt/.transcoder/working";
    public string StagingRoot { get; set; } = "/mnt/.transcoder/staging";
    public bool AutoCreateStorageFolders { get; set; } = true;

    // When false, originals are only moved to a temporary rollback file during replace
    // and are deleted immediately after the staged file has been installed.
    public bool KeepOriginalsQuarantine { get; set; } = true;

    // Optional Unraid physical-disk manifest produced by scripts/unraid-export-storage-map.sh.
    // The server only uses this to schedule reads; file operations still use normal user-share paths.
    public string? StorageMapPath { get; set; }

    // When enabled, cleanup/transcode leasing prefers jobs from the least-busy physical disk/storage key.
    public bool StorageAwareScheduling { get; set; } = false;
    public int MaxActiveSourceJobsPerStorageKey { get; set; } = 1;
    public bool PreferLeastBusyStorageKey { get; set; } = true;
    public bool UseParentFolderForUnknownStorageKey { get; set; } = true;
    public int UnknownStorageKeyFolderDepth { get; set; } = 2;
}


public sealed class ActiveHoursOptions
{
    public bool Enabled { get; set; } = true;
    public string TimeZoneId { get; set; } = "Europe/London";
    public string Start { get; set; } = "08:30";
    public string Stop { get; set; } = "02:00";
    public int StopNewWorkMinutesBefore { get; set; } = 30;
}

public sealed class WorkerRequirementOptions
{
    public bool RejectWorkersWithMissingRequiredTools { get; set; } = true;
    public string? MinimumFfmpegVersion { get; set; }
    public string? MinimumFfprobeVersion { get; set; }
    public bool RequireFfmpegForTranscoderRole { get; set; } = true;
    public bool RequireFfprobeForProberRole { get; set; } = true;
    public bool RequireFfprobeForValidatorRole { get; set; } = true;
}
