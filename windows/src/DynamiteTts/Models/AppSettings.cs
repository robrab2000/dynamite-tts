using System;
using System.Runtime.Intrinsics.X86;

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
    public string ChatModel { get; set; } = string.Empty;
    public int ExplainSentenceCount { get; set; } = 3;
    public ExplainAudience ExplainAudience { get; set; } = ExplainAudience.Technical;
    public string AudioDeviceId { get; set; } = string.Empty; // Empty string = System Default Device
    public bool RunAtStartup { get; set; } = false;
    public bool PlaySoundOnStop { get; set; } = true;
    public bool ShowStatusBubble { get; set; } = true; // status pill at the top of the screen while reading/synthesizing/speaking
    // Property names below keep their historical JSON names for settings.json compatibility.
    public string EngineMode { get; set; } = DefaultEngineMode; // "DirectML" = in-process Kokoro (CPU + CUDA), "Phonon" = in-process Phonon (CPU), "LemonadeServer" = HTTP
    public bool UseDirectMlAcceleration { get; set; } = true; // allow the NVIDIA GPU (CUDA) to be used
    public int DirectMlDeviceId { get; set; } = -1; // -1 = Auto (GPU when available), -2 = CPU only, 0.. = CUDA device ordinal
    public string DirectMlModelPrecision { get; set; } = "float32"; // "float32" (preferred) or "float16"
    public int GpuIdleUnloadMinutes { get; set; } = 5; // unload the CUDA session after this many idle minutes so the dGPU can sleep
    public string PhononVoice { get; set; } = "default"; // "default" = the checkpoint's own voice; otherwise a name from the Phonon voice catalogue
    public string PhononModelDir { get; set; } = string.Empty; // empty = bundled/downloaded phonon-31m-en; otherwise a checkpoint folder (e.g. the gated 40M multilingual model)
    public string PhononLanguage { get; set; } = "en"; // text normalization language for Phonon (en, fr, de, es, pt, ...)

    public const string EngineKokoro = "DirectML";
    public const string EnginePhonon = "Phonon";
    public const string EngineLemonade = "LemonadeServer";
    /// <summary>Phonon, except on CPUs without AVX2, which its x86-64-v3 build cannot run on.</summary>
    public static string DefaultEngineMode => Avx2.IsSupported ? EnginePhonon : EngineKokoro;
    public bool IsPhononEngine => string.Equals(EngineMode, EnginePhonon, StringComparison.OrdinalIgnoreCase);
    public bool IsKokoroEngine => string.Equals(EngineMode, EngineKokoro, StringComparison.OrdinalIgnoreCase);
    public bool IsLocalEngine => IsPhononEngine || IsKokoroEngine;

    public static int ClampExplainSentenceCount(int value) => Math.Clamp(value, 1, 8);

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
            ChatModel = ChatModel,
            ExplainSentenceCount = ClampExplainSentenceCount(ExplainSentenceCount),
            ExplainAudience = ExplainAudience,
            AudioDeviceId = AudioDeviceId,
            RunAtStartup = RunAtStartup,
            PlaySoundOnStop = PlaySoundOnStop,
            ShowStatusBubble = ShowStatusBubble,
            EngineMode = EngineMode,
            UseDirectMlAcceleration = UseDirectMlAcceleration,
            DirectMlDeviceId = DirectMlDeviceId,
            DirectMlModelPrecision = DirectMlModelPrecision,
            GpuIdleUnloadMinutes = GpuIdleUnloadMinutes,
            PhononVoice = PhononVoice,
            PhononModelDir = PhononModelDir,
            PhononLanguage = PhononLanguage
        };
    }
}
