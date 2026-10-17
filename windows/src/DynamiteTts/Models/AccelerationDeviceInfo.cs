namespace DynamiteTts.Models;

public enum AccelerationDeviceKind
{
    Auto = 0,
    Gpu = 1,
    NpuUnavailable = 2,
    CpuOnly = 3
}

/// <summary>An entry in the Settings window's accelerator picker. DeviceId is persisted in settings.json.</summary>
public class AccelerationDeviceInfo
{
    public int DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AccelerationDeviceKind Kind { get; set; } = AccelerationDeviceKind.Gpu;
    public bool IsNpu => Kind == AccelerationDeviceKind.NpuUnavailable;
    public bool IsSelectable => Kind != AccelerationDeviceKind.NpuUnavailable;

    public override string ToString() => Name;
}
