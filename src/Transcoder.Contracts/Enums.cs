namespace Transcoder.Contracts;

public enum ProcessingMode
{
    Disabled,
    ScanOnly,
    ProbeOnly,
    PlanOnly,
    PlanAndReview,
    TranscodeToStaging,
    ReplaceApproved
}

public enum ProcessingStrategy
{
    TranscodeOnly,
    CleanupOnly,
    CleanupThenTranscode,
    CleanupAndTranscodeSinglePass
}

public enum TranscodeEnginePolicy
{
    PreferGpu,
    GpuOnly,
    PreferCpu,
    CpuOnly,
    Either
}

public enum AudioDuplicateMode
{
    KeepBestPerLanguage,
    KeepBestAndStereoPerLanguage,
    KeepAllAllowed
}

public enum PremiumAudioMode
{
    KeepAllPremium,
    KeepBestPremiumOnly,
    KeepBestPremiumAndCompatibility,
    KeepBestTwoPremiumAndCompatibility,
    KeepCompatibilityOnly
}

public enum PremiumAudioRanking
{
    PreferAtmosThenTrueHd,
    PreferAtmosThenDts,
    PreferTrueHd,
    PreferDts
}

public enum EncoderEngine
{
    Unknown = 0,
    Copy = 1,
    Cpu = 2,
    Gpu = 3,
    Either = 4
}

[Flags]
public enum WorkerRole
{
    None = 0,
    Prober = 1,
    // Runs stream cleanup/remux jobs only. This is safe for workers without a GPU.
    Cleanup = 8,
    // Runs video transcode jobs. Use this for workers with GPU access when profiles require GPU encoders.
    Transcoder = 2,
    Validator = 4
}

public enum WorkerState
{
    Registered,
    PathCheckRequired,
    PathCheckFailed,
    PartialPathAccess,
    Online,
    Unresponsive,
    Lost,
    Draining,
    Disabled,
    RequirementsFailed
}

public enum WorkerControlState
{
    Normal,
    Drain,
    DrainThenExit,
    DrainThenShutdown,
    Disabled
}

public enum JobType
{
    Probe = 0,
    PlanReview = 1,
    Transcode = 2,
    ValidateOutput = 3,
    Cleanup = 4,
    ReplaceOriginal = 5
}

public enum JobStatus
{
    Queued,
    Leased,
    Running,
    Completed,
    Failed,
    Cancelled,
    Expired
}

public enum JobQueuePriority
{
    Low = 0,
    Normal = 50,
    High = 100,
    Urgent = 200
}

public enum MediaStatus
{
    New = 0,
    ProbeQueued = 1,
    Probed = 2,
    Planning = 3,
    NeedsReview = 4,
    ReadyToTranscode = 5,
    Transcoding = 6,
    Staged = 7,
    Approved = 8,
    Rejected = 9,
    Skipped = 10,
    Complete = 11,
    ReadyToCleanup = 12,
    Cleaning = 13,
    StagedCleaned = 14,
    ReplacedCleaned = 15,
    ReplacedTranscoded = 16,
    ReplaceFailed = 17,
    Missing = 18
}

public enum ReviewType
{
    HumanInterventionRequired,
    PlanReviewFailed,
    UnknownMetadata,
    PolicyAmbiguous,
    OutputValidationWarning,
    ManualApprovalRequired
}

public enum ReviewSeverity
{
    Info,
    Warning,
    Blocking
}

public enum UnknownTrackAction
{
    Keep,
    Remove,
    NeedsReview
}

public enum MetadataSource
{
    None,
    FileProbe,
    Radarr,
    Sonarr,
    Tmdb,
    Imdb,
    Manual,
    LibraryDefault
}

public enum TranscodeWorkingMode
{
    LocalThenCopyToStaging,
    DirectToStaging
}
