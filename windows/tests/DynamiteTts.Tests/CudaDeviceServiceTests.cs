using System.Linq;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class CudaDeviceServiceTests
{
    [Fact]
    public void GetAvailableDevices_AlwaysOffersAutoCpuOnlyAndMarksNpuUnavailable()
    {
        var service = new CudaDeviceService();
        var devices = service.GetAvailableDevices();

        Assert.Contains(devices, d => d.DeviceId == CudaDeviceService.AutoDeviceId && d.Kind == AccelerationDeviceKind.Auto);
        Assert.Contains(devices, d => d.DeviceId == CudaDeviceService.CpuOnlyDeviceId && d.Kind == AccelerationDeviceKind.CpuOnly);

        var npuEntries = devices.Where(d => d.Kind == AccelerationDeviceKind.NpuUnavailable).ToList();
        Assert.NotEmpty(npuEntries);
        Assert.All(npuEntries, d => Assert.Equal(CudaDeviceService.NpuUnavailableDeviceId, d.DeviceId));
        Assert.All(npuEntries, d => Assert.False(d.IsSelectable));
    }

    [Fact]
    public void Probe_NeverThrows_AndGpuEntriesMatchProbe()
    {
        var probe = CudaDeviceService.Probe();
        var gpus = new CudaDeviceService().GetAvailableDevices().Where(d => d.Kind == AccelerationDeviceKind.Gpu).ToList();

        Assert.Equal(probe.Devices.Count, gpus.Count);
        if (!probe.IsUsable)
            Assert.False(string.IsNullOrWhiteSpace(probe.Unavailability));
    }

    [Fact]
    public void ResolveCudaOrdinal_HonorsCpuOnlyAndFallsBackToFirstGpu()
    {
        Assert.Equal(-1, CudaDeviceService.ResolveCudaOrdinal(CudaDeviceService.CpuOnlyDeviceId));

        var probe = CudaDeviceService.Probe();
        var auto = CudaDeviceService.ResolveCudaOrdinal(CudaDeviceService.AutoDeviceId, probe);
        var stale = CudaDeviceService.ResolveCudaOrdinal(42, probe); // e.g. an old DirectML adapter index
        if (probe.IsUsable)
        {
            Assert.Equal(probe.Devices[0].Ordinal, auto);
            Assert.Equal(probe.Devices[0].Ordinal, stale);
        }
        else
        {
            Assert.Equal(-1, auto);
            Assert.Equal(-1, stale);
        }
    }

    [Fact]
    public void CudaRuntimeManifest_IsConsistent()
    {
        Assert.True(CudaRuntimeService.TotalDownloadBytes > 900_000_000);
        Assert.All(CudaRuntimeService.Packages, p =>
        {
            Assert.Equal(64, p.Sha256.Length);
            Assert.StartsWith("https://files.pythonhosted.org/", p.Url);
            Assert.NotEmpty(p.Dlls);
        });
    }
}
