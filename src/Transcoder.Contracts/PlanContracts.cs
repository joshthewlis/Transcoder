namespace Transcoder.Contracts;

public sealed class TranscodePlanDto
{
    public int PlanVersion { get; set; } = 1;
    public string PlanKind { get; set; } = "Transcode";
    public ProcessingStrategy ProcessingStrategy { get; set; } = ProcessingStrategy.TranscodeOnly;
    public long MediaId { get; set; }
    public int LibraryId { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public string StagingOutputPath { get; set; } = string.Empty;
    public string TargetVideoCodec { get; set; } = string.Empty;
    public string VideoProfile { get; set; } = string.Empty;
    public TranscodeEnginePolicy TranscodeEnginePolicy { get; set; } = TranscodeEnginePolicy.PreferGpu;
    public EncoderEngine RequiredEncoderEngine { get; set; } = EncoderEngine.Unknown;
    public string? RequiredEncoder { get; set; }
    public string? EncoderSelectionReason { get; set; }
    public long InputFileSizeBytes { get; set; }
    public long? EstimatedRemovedBytes { get; set; }
    public long? EstimatedOutputSizeBytes { get; set; }
    public bool EstimatedSavingsComplete { get; set; }
    public bool CleanupRequired { get; set; }
    public List<string> CleanupReasons { get; set; } = [];
    public List<string> SavingsNotes { get; set; } = [];
    public string PlanHash { get; set; } = string.Empty;
    public List<StreamPlanDto> Streams { get; set; } = [];
    public List<string> FfmpegArgs { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> BlockingReasons { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class StreamPlanDto
{
    public int SourceStreamIndex { get; set; }
    public string StreamType { get; set; } = string.Empty;
    public string? CodecName { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool Default { get; set; }
    public bool Forced { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public bool ProtectedAudio { get; set; }
    public bool CompatibilityAudio { get; set; }
    public int? OutputStreamIndex { get; set; }
    public int? OutputTypeIndex { get; set; }
    public bool OutputDefault { get; set; }
    public string Action { get; set; } = "Remove";
    public string Reason { get; set; } = string.Empty;
    public long? EstimatedSizeBytes { get; set; }
    public long? EstimatedSavingBytes { get; set; }
    public string? SizeEstimateSource { get; set; }
}

public sealed class PlanReviewResultDto
{
    public string ReviewStatus { get; set; } = "Approved";
    public List<string> Messages { get; set; } = [];
    public long ReprobedFileSizeBytes { get; set; }
    public DateTime ReprobedLastModifiedUtc { get; set; }
}
