using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

/// <summary>
/// Enumerates DirectML-compatible DXGI display adapters (GPUs).
/// The AMD Ryzen AI NPU is not usable for Kokoro TTS with current DirectML/Lemonade stacks.
/// </summary>
public class DirectMlDeviceService
{
    [ComImport]
    [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, out IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
    }

    [ComImport]
    [Guid("2411e03e-86e3-4f63-97e8-aa57b182f239")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, out IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC pDesc);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppFactory);

    public const string NpuUnavailableReason =
        "Kokoro TTS cannot run on the AMD Ryzen AI NPU today. " +
        "Use your NVIDIA RTX or AMD Radeon GPU below for hardware acceleration.";

    public IReadOnlyList<DirectMlDeviceInfo> GetAvailableDevices()
    {
        var adapters = EnumerateGpuAdapters();
        var preferred = ResolvePreferredGpuDeviceId(adapters);

        var list = new List<DirectMlDeviceInfo>
        {
            new()
            {
                DeviceId = -1,
                Name = preferred >= 0
                    ? $"Auto (Prefer fastest GPU — currently adapter {preferred})"
                    : "Auto (Primary GPU via DirectML)",
                Description = "Picks NVIDIA dGPU when present, otherwise the first DXGI GPU.",
                Kind = DirectMlDeviceKind.Auto
            }
        };

        foreach (var adapter in adapters)
        {
            list.Add(adapter);
        }

        list.Add(new DirectMlDeviceInfo
        {
            DeviceId = -100,
            Name = "AMD Ryzen AI NPU — Not supported for Kokoro TTS",
            Description = NpuUnavailableReason,
            Kind = DirectMlDeviceKind.NpuUnavailable
        });

        return list;
    }

    public static int ResolvePreferredGpuDeviceId()
    {
        return ResolvePreferredGpuDeviceId(new DirectMlDeviceService().EnumerateGpuAdapters());
    }

    private static int ResolvePreferredGpuDeviceId(IReadOnlyList<DirectMlDeviceInfo> adapters)
    {
        var nvidia = -1;
        var amd = -1;
        var first = -1;

        foreach (var adapter in adapters)
        {
            if (adapter.Kind != DirectMlDeviceKind.Gpu) continue;
            if (first < 0) first = adapter.DeviceId;

            var desc = adapter.Description;
            if (nvidia < 0 &&
                (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                 desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                 desc.Contains("RTX", StringComparison.OrdinalIgnoreCase)))
            {
                nvidia = adapter.DeviceId;
            }
            else if (amd < 0 &&
                     (desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                      desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase)))
            {
                amd = adapter.DeviceId;
            }
        }

        // Discrete NVIDIA is typically much faster for Kokoro than iGPU.
        if (nvidia >= 0) return nvidia;
        if (amd >= 0) return amd;
        return first >= 0 ? first : 0;
    }

    private List<DirectMlDeviceInfo> EnumerateGpuAdapters()
    {
        var list = new List<DirectMlDeviceInfo>();
        var factoryGuid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        IntPtr pFactory = IntPtr.Zero;

        try
        {
            int hr = CreateDXGIFactory(factoryGuid, out pFactory);
            if (hr != 0 || pFactory == IntPtr.Zero)
                return list;

            var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(pFactory);
            uint adapterIndex = 0;

            while (true)
            {
                IntPtr pAdapter = IntPtr.Zero;
                hr = factory.EnumAdapters(adapterIndex, out pAdapter);
                if (hr != 0 || pAdapter == IntPtr.Zero)
                    break;

                try
                {
                    var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(pAdapter);
                    if (adapter.GetDesc(out var desc) != 0)
                    {
                        adapterIndex++;
                        continue;
                    }

                    var descStr = desc.Description?.Trim() ?? string.Empty;
                    if (desc.VendorId == 0x1414 ||
                        descStr.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase) ||
                        descStr.Contains("Software Adapter", StringComparison.OrdinalIgnoreCase) ||
                        descStr.Contains("Meta Virtual", StringComparison.OrdinalIgnoreCase))
                    {
                        adapterIndex++;
                        continue;
                    }

                    var vendorHint = desc.VendorId switch
                    {
                        0x10DE => "NVIDIA",
                        0x1002 => "AMD",
                        0x8086 => "Intel",
                        _ => "GPU"
                    };

                    list.Add(new DirectMlDeviceInfo
                    {
                        DeviceId = (int)adapterIndex,
                        Name = $"GPU {adapterIndex}: {descStr} [{vendorHint}]",
                        Description = descStr,
                        Kind = DirectMlDeviceKind.Gpu
                    });
                }
                catch { }
                finally
                {
                    if (pAdapter != IntPtr.Zero)
                        Marshal.Release(pAdapter);
                }

                adapterIndex++;
            }
        }
        catch { }
        finally
        {
            if (pFactory != IntPtr.Zero)
                Marshal.Release(pFactory);
        }

        return list;
    }
}
