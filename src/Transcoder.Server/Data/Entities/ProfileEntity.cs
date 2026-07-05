namespace Transcoder.Server.Data.Entities;

public sealed class ProfileEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetCodec { get; set; } = string.Empty;
    public string Encoder { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "[]";
    public string RequiredEncodersJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
}
