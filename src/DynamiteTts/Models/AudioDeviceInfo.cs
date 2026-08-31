namespace DynamiteTts.Models;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    public override string ToString() => Name;
}
