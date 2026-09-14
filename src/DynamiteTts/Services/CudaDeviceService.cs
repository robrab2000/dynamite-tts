using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

/// <summary>
/// Enumerates NVIDIA GPUs through the CUDA driver API (<c>nvcuda.dll</c>, shipped with the display
/// driver) and builds the accelerator list for the Settings window. Device ordinals here are the
/// same ordinals ONNX Runtime's CUDA provider uses.
/// </summary>
public class CudaDeviceService
{
    public sealed record CudaDevice(int Ordinal, string Name, ulong TotalMemoryBytes, int ComputeMajor, int ComputeMinor);

    public sealed record ProbeResult(IReadOnlyList<CudaDevice> Devices, int DriverCudaVersion, string? Unavailability)
    {
        public bool IsUsable => Unavailability == null && Devices.Count > 0;
    }

    /// <summary>ONNX Runtime 1.29's CUDA provider is built against CUDA 13, which needs driver 580 or newer.</summary>
    public const int MinimumDriverCudaVersion = 13000;

    public const int AutoDeviceId = -1;
    public const int CpuOnlyDeviceId = -2;
    public const int NpuUnavailableDeviceId = -100;

    public const string NpuUnavailableReason =
        "Kokoro TTS cannot run on the AMD Ryzen AI NPU today (no Vitis AI execution provider without AMD's Ryzen AI Software, " +
        "and the model's operators are not covered). Use Auto or the NVIDIA GPU entry instead.";

    private static readonly object ProbeLock = new();
    private static ProbeResult? _cached;

    [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuDriverGetVersion(out int version);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName(byte[] name, int length, int device);
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceTotalMem_v2")] private static extern int cuDeviceTotalMem(out ulong bytes, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport("nvcuda.dll", EntryPoint = "cuDevicePrimaryCtxReset_v2")] private static extern int cuDevicePrimaryCtxReset(int device);

    private const int CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR = 75;
    private const int CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR = 76;

    /// <summary>Probes once per process; use <see cref="Refresh"/> to re-enumerate.</summary>
    public static ProbeResult Probe()
    {
        lock (ProbeLock)
        {
            return _cached ??= ProbeCore();
        }
    }

    public static ProbeResult Refresh()
    {
        lock (ProbeLock)
        {
            _cached = ProbeCore();
            return _cached;
        }
    }

    private static ProbeResult ProbeCore()
    {
        var devices = new List<CudaDevice>();
        try
        {
            var rc = cuInit(0);
            if (rc == 100) return new ProbeResult(devices, 0, "No CUDA-capable NVIDIA GPU detected");
            if (rc != 0) return new ProbeResult(devices, 0, $"CUDA driver initialization failed (error {rc})");

            cuDriverGetVersion(out var driverVersion);
            if (driverVersion < MinimumDriverCudaVersion)
            {
                return new ProbeResult(devices, driverVersion,
                    $"NVIDIA driver supports CUDA {driverVersion / 1000}.{driverVersion % 1000 / 10} but CUDA 13 (driver 580 or newer) is required");
            }

            if (cuDeviceGetCount(out var count) != 0 || count <= 0)
                return new ProbeResult(devices, driverVersion, "No CUDA-capable NVIDIA GPU detected");

            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                if (cuDeviceGet(out var dev, ordinal) != 0) continue;

                var nameBuffer = new byte[256];
                var name = cuDeviceGetName(nameBuffer, nameBuffer.Length, dev) == 0
                    ? Encoding.ASCII.GetString(nameBuffer).TrimEnd('\0').Trim()
                    : $"NVIDIA GPU {ordinal}";

                cuDeviceTotalMem(out var memory, dev);
                cuDeviceGetAttribute(out var major, CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR, dev);
                cuDeviceGetAttribute(out var minor, CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR, dev);

                devices.Add(new CudaDevice(ordinal, name, memory, major, minor));
            }

            return new ProbeResult(devices, driverVersion, devices.Count > 0 ? null : "No CUDA-capable NVIDIA GPU detected");
        }
        catch (DllNotFoundException)
        {
            return new ProbeResult(devices, 0, "NVIDIA display driver not installed (nvcuda.dll not found)");
        }
        catch (Exception ex)
        {
            return new ProbeResult(devices, 0, $"CUDA driver probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps a settings device id to a CUDA ordinal: -1 (Auto) and unknown ids pick the first GPU;
    /// returns -1 when the machine has no usable NVIDIA GPU or CPU-only was requested.
    /// </summary>
    public static int ResolveCudaOrdinal(int requestedDeviceId, ProbeResult? probe = null)
    {
        if (requestedDeviceId == CpuOnlyDeviceId) return -1;
        probe ??= Probe();
        if (!probe.IsUsable) return -1;

        foreach (var d in probe.Devices)
            if (d.Ordinal == requestedDeviceId) return d.Ordinal;

        return probe.Devices[0].Ordinal;
    }

    /// <summary>
    /// Releases the device's primary CUDA context after the last session on it is disposed, so an
    /// idle app in the tray does not keep the discrete GPU powered up. Safe to call when nothing
    /// is using the device.
    /// </summary>
    public static void ReleasePrimaryContext(int ordinal)
    {
        try
        {
            if (cuDeviceGet(out var device, ordinal) == 0)
                cuDevicePrimaryCtxReset(device);
        }
        catch { }
    }

    public IReadOnlyList<AccelerationDeviceInfo> GetAvailableDevices()
    {
        var probe = Refresh();
        var list = new List<AccelerationDeviceInfo>();

        var autoTarget = probe.IsUsable
            ? $"currently {probe.Devices[0].Name}"
            : probe.Unavailability ?? "no NVIDIA GPU";
        list.Add(new AccelerationDeviceInfo
        {
            DeviceId = AutoDeviceId,
            Name = $"Auto — NVIDIA GPU via CUDA when available, otherwise CPU ({autoTarget})",
            Description = "Loads the GPU session on first use and unloads it after a few idle minutes; the CPU covers the first sentences meanwhile.",
            Kind = AccelerationDeviceKind.Auto
        });

        foreach (var d in probe.Devices)
        {
            list.Add(new AccelerationDeviceInfo
            {
                DeviceId = d.Ordinal,
                Name = $"GPU {d.Ordinal}: {d.Name} (CUDA, {d.TotalMemoryBytes / (1024.0 * 1024 * 1024):F0} GB)",
                Description = d.Name,
                Kind = AccelerationDeviceKind.Gpu
            });
        }

        list.Add(new AccelerationDeviceInfo
        {
            DeviceId = CpuOnlyDeviceId,
            Name = "CPU only — never touch the GPU (about 0.7 s to first audio)",
            Description = "Keeps the discrete GPU asleep; no runtime download.",
            Kind = AccelerationDeviceKind.CpuOnly
        });

        list.Add(new AccelerationDeviceInfo
        {
            DeviceId = NpuUnavailableDeviceId,
            Name = "AMD Ryzen AI NPU — not supported for Kokoro TTS",
            Description = NpuUnavailableReason,
            Kind = AccelerationDeviceKind.NpuUnavailable
        });

        return list;
    }
}
