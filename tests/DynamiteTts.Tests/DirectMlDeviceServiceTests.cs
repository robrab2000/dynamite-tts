using System.Linq;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class DirectMlDeviceServiceTests
{
    [Fact]
    public void GetAvailableDevices_ReturnsAutoAndMarksNpuUnavailable()
    {
        var service = new DirectMlDeviceService();
        var devices = service.GetAvailableDevices();

        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.DeviceId == -1 && d.Kind == DirectMlDeviceKind.Auto);

        var npuEntries = devices.Where(d => d.Kind == DirectMlDeviceKind.NpuUnavailable).ToList();
        Assert.NotEmpty(npuEntries);
        Assert.All(npuEntries, d => Assert.Equal(-100, d.DeviceId));
    }

    [Fact]
    public void ResolvePreferredGpuDeviceId_ReturnsNonNegative()
    {
        var id = DirectMlDeviceService.ResolvePreferredGpuDeviceId();
        Assert.True(id >= 0);
    }
}
