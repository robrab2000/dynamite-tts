namespace DynamiteTts.Models;

public enum DirectMlDeviceKind
{
    Auto = 0,
    Gpu = 1,
    NpuUnavailable = 2
}

public class DirectMlDeviceInfo
{
    public int DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DirectMlDeviceKind Kind { get; set; } = DirectMlDeviceKind.Gpu;
    public bool IsNpu => Kind == DirectMlDeviceKind.NpuUnavailable;

    public override string ToString() => Name;
}
