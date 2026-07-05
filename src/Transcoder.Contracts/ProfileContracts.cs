namespace Transcoder.Contracts;

public sealed class TranscodeProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetCodec { get; set; } = string.Empty;
    public string Encoder { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public List<string> RequiredEncoders { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public class CreateProfileRequest
{
    public string Name { get; set; } = string.Empty;
    public string TargetCodec { get; set; } = string.Empty;
    public string Encoder { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public List<string> RequiredEncoders { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class UpdateProfileRequest : CreateProfileRequest
{
}
