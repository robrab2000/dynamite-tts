using System;
using DynamiteTts.Models;

namespace DynamiteTts.Models;

public class AppSettings
{
    public string SpeechEndpoint { get; set; } = "http://localhost:13305/api/v1/audio/speech";
    public string Model { get; set; } = "kokoro-v1";
    public string Voice { get; set; } = "coral";
    public double Speed { get; set; } = 1.0;
    public string ResponseFormat { get; set; } = "mp3";
    public HotkeyConfig Hotkey { get; set; } = HotkeyConfig.Default;
    public string AudioDeviceId { get; set; } = string.Empty; // Empty string = System Default Device
    public bool RunAtStartup { get; set; } = false;
    public bool PlaySoundOnStop { get; set; } = true;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            SpeechEndpoint = SpeechEndpoint,
            Model = Model,
            Voice = Voice,
            Speed = Speed,
            ResponseFormat = ResponseFormat,
            Hotkey = new HotkeyConfig
            {
                Modifiers = Hotkey.Modifiers,
                Key = Hotkey.Key
            },
            AudioDeviceId = AudioDeviceId,
            RunAtStartup = RunAtStartup,
            PlaySoundOnStop = PlaySoundOnStop
        };
    }
}
