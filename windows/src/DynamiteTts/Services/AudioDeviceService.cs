using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

public class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new()
            {
                Id = string.Empty,
                Name = "System Default Output Device",
                IsDefault = true
            }
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = endpoint.ID,
                    Name = endpoint.FriendlyName,
                    IsDefault = false
                });
            }
        }
        catch
        {
            // Fall back to just the default item
        }

        return devices;
    }
}
