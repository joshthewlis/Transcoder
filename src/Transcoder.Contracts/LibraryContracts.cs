namespace Transcoder.Contracts;

public sealed class LibraryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public MediaStatus? DefaultMediaStatus { get; set; }
    public LibraryPolicyDto Policy { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CreateLibraryRequest
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public LibraryPolicyDto Policy { get; set; } = new();
}

public sealed class UpdateLibraryRequest
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public LibraryPolicyDto Policy { get; set; } = new();
}

public sealed class ScanLibraryRequest
{
    public bool Force { get; set; }
    public bool CreateProbeJobs { get; set; } = true;
}

public sealed class LibraryScanStatusDto
{
    public int LibraryId { get; set; }
    public bool IsScanning { get; set; }
    public long FoldersScanned { get; set; }
    public long FilesDiscovered { get; set; }
    public long MediaItemsCreated { get; set; }
    public long MediaItemsUpdated { get; set; }
    public long ProbeJobsCreated { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? LastError { get; set; }
}


public sealed class LibraryWatchStatusDto
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool WatchEnabled { get; set; }
    public bool WatcherActive { get; set; }
    public bool IncludeSubdirectories { get; set; }
    public int PendingFileCount { get; set; }
    public DateTime? LastEventUtc { get; set; }
    public DateTime? LastProcessedUtc { get; set; }
    public DateTime? LastScanQueuedUtc { get; set; }
    public DateTime? NextPeriodicScanUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LibraryPolicyDto
{
    public ProcessingStrategy ProcessingStrategy { get; set; } = ProcessingStrategy.TranscodeOnly;
    public ExecutionPolicyDto Execution { get; set; } = new();
    public VideoPolicyDto Video { get; set; } = new();
    public AudioPolicyDto Audio { get; set; } = new();
    public SubtitlePolicyDto Subtitles { get; set; } = new();
    public OutputPolicyDto Output { get; set; } = new();
    public ReviewPolicyDto Review { get; set; } = new();
    public MetadataPolicyDto Metadata { get; set; } = new();
    public WatchPolicyDto Watch { get; set; } = new();
    public List<string> IgnoreFolderNames { get; set; } = [".transcoder", "@eaDir", "lost+found", "sample", "samples"];
    public List<string> AllowedExtensions { get; set; } = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".m2ts"];
}


public sealed class ExecutionPolicyDto
{
    public bool GenerateCleanupJobs { get; set; } = true;
    public bool GenerateTranscodeJobs { get; set; }
}

public sealed class WatchPolicyDto
{
    public bool Enabled { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = true;
    public bool ScanOnStartup { get; set; }
    public int SettleSeconds { get; set; } = 300;
    public int MinimumFileAgeSeconds { get; set; } = 120;
    public int PeriodicRescanMinutes { get; set; } = 120;
    public List<string> IgnoreTemporaryExtensions { get; set; } = [".part", ".partial", ".tmp", ".download", ".!qb", ".crdownload"];
}

public sealed class VideoPolicyDto
{
    public string TargetCodec { get; set; } = "hevc";
    public string Profile { get; set; } = "hevc_nvenc_balanced";
    public TranscodeEnginePolicy TranscodeEngine { get; set; } = TranscodeEnginePolicy.PreferGpu;
    public List<string> OnlyConvertCodecs { get; set; } = ["h264"];
    public List<string> SkipCodecs { get; set; } = ["hevc", "av1"];
}

public sealed class AudioPolicyDto
{
    public List<string> KeepLanguages { get; set; } = ["eng"];
    public bool KeepOriginalLanguage { get; set; } = true;
    public bool OriginalLanguageFirst { get; set; } = true;
    public bool FallbackToDefault { get; set; } = true;
    public bool FallbackToFirst { get; set; } = true;
    public string TargetCodec { get; set; } = "copy";
    public string? TargetProfile { get; set; }
    public bool RemoveCommentary { get; set; } = true;
    public bool RemoveDescriptiveAudio { get; set; } = true;
    public AudioDuplicateMode DuplicateMode { get; set; } = AudioDuplicateMode.KeepBestPerLanguage;
    public int PreferredChannels { get; set; } = 6;
    public int MaximumChannels { get; set; } = 8;
    public bool PreserveSpatialAudio { get; set; } = true;
    public bool PreserveLosslessAudio { get; set; } = true;
    public bool KeepCompatibilityTrack { get; set; } = true;
    public PremiumAudioMode PremiumMode { get; set; } = PremiumAudioMode.KeepBestPremiumAndCompatibility;
    public PremiumAudioRanking PremiumRanking { get; set; } = PremiumAudioRanking.PreferAtmosThenTrueHd;
    public bool ReviewDifferentMixTitles { get; set; } = true;
    public UnknownTrackAction UnknownAudioAction { get; set; } = UnknownTrackAction.NeedsReview;
}

public sealed class SubtitlePolicyDto
{
    public List<string> KeepLanguages { get; set; } = ["eng"];
    public bool KeepOriginalLanguage { get; set; }
    public bool KeepForced { get; set; } = true;
    public UnknownTrackAction UnknownSubtitleAction { get; set; } = UnknownTrackAction.NeedsReview;
}

public sealed class OutputPolicyDto
{
    public bool StagingOnly { get; set; } = true;
    public bool MirrorFolderStructure { get; set; } = true;
    public bool ReplaceOriginals { get; set; }
}

public sealed class ReviewPolicyDto
{
    public bool PlanReviewRequired { get; set; } = true;
    public bool HumanReviewRequiredForUnknownMetadata { get; set; } = true;
}

public sealed class MetadataPolicyDto
{
    public List<MetadataSource> MetadataSourcePriority { get; set; } = [MetadataSource.Radarr, MetadataSource.Sonarr, MetadataSource.Tmdb, MetadataSource.FileProbe];
    public int? RadarrIntegrationId { get; set; }
    public int? SonarrIntegrationId { get; set; }
    public bool UseTmdb { get; set; } = true;
    public bool MatchByPath { get; set; } = true;
    public string? DefaultOriginalLanguage { get; set; }
    public bool InferOriginalLanguageFromSingleAudioLanguage { get; set; } = true;
    public bool RequireOriginalLanguageWhenPolicyUsesIt { get; set; } = true;
}
