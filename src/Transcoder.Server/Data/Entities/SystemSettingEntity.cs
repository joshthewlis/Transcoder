namespace Transcoder.Server.Data.Entities;

public sealed class SystemSettingEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
