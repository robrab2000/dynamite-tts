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
    public HotkeyConfig ModeToggleHotkey { get; set; } = HotkeyConfig.ModeToggleDefault;
    public SpeakMode SpeakMode { get; set; } = SpeakMode.Verbatim;
    public string ChatEndpoint { get; set; } = "http://localhost:13305/api/v1/chat/completions";
    public string SummaryModel { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty; // Empty string = System Default Device
    public bool RunAtStartup { get; set; } = false;
    public bool PlaySoundOnStop { get; set; } = true;
    public bool ShowStatusBubble { get; set; } = true; // status pill at the top of the screen while reading/synthesizing/speaking
    // Property names below keep their historical JSON names for settings.json compatibility.
    public string EngineMode { get; set; } = "DirectML"; // "DirectML" = in-process local engine (CPU + CUDA), "LemonadeServer" = HTTP
    public bool UseDirectMlAcceleration { get; set; } = true; // allow the NVIDIA GPU (CUDA) to be used
    public int DirectMlDeviceId { get; set; } = -1; // -1 = Auto (GPU when available), -2 = CPU only, 0.. = CUDA device ordinal
    public string DirectMlModelPrecision { get; set; } = "float32"; // "float32" (preferred) or "float16"
    public int GpuIdleUnloadMinutes { get; set; } = 5; // unload the CUDA session after this many idle minutes so the dGPU can sleep

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
            ModeToggleHotkey = new HotkeyConfig
            {
                Modifiers = ModeToggleHotkey.Modifiers,
                Key = ModeToggleHotkey.Key
            },
            SpeakMode = SpeakMode,
            ChatEndpoint = ChatEndpoint,
            SummaryModel = SummaryModel,
            AudioDeviceId = AudioDeviceId,
            RunAtStartup = RunAtStartup,
            PlaySoundOnStop = PlaySoundOnStop,
            ShowStatusBubble = ShowStatusBubble,
            EngineMode = EngineMode,
            UseDirectMlAcceleration = UseDirectMlAcceleration,
            DirectMlDeviceId = DirectMlDeviceId,
            DirectMlModelPrecision = DirectMlModelPrecision,
            GpuIdleUnloadMinutes = GpuIdleUnloadMinutes
        };
    }
}
