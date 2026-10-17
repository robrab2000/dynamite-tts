using System.Collections.Generic;

namespace DynamiteTts.Models;

public class VoicePreset
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public override string ToString() => DisplayName;

    public static IReadOnlyList<VoicePreset> Presets { get; } = new List<VoicePreset>
    {
        // OpenAI Compatible / Standard
        new() { Id = "coral", DisplayName = "Coral (OpenAI / Warm Female)", Category = "OpenAI Compatible" },
        new() { Id = "alloy", DisplayName = "Alloy (OpenAI / Neutral)", Category = "OpenAI Compatible" },
        new() { Id = "echo", DisplayName = "Echo (OpenAI / Confident Male)", Category = "OpenAI Compatible" },
        new() { Id = "fable", DisplayName = "Fable (OpenAI / British Accent)", Category = "OpenAI Compatible" },
        new() { Id = "onyx", DisplayName = "Onyx (OpenAI / Deep Male)", Category = "OpenAI Compatible" },
        new() { Id = "nova", DisplayName = "Nova (OpenAI / Energetic Female)", Category = "OpenAI Compatible" },
        new() { Id = "shimmer", DisplayName = "Shimmer (OpenAI / Soft Female)", Category = "OpenAI Compatible" },
        new() { Id = "ash", DisplayName = "Ash (OpenAI / Conversational Male)", Category = "OpenAI Compatible" },
        new() { Id = "sage", DisplayName = "Sage (OpenAI / Calm Female)", Category = "OpenAI Compatible" },

        // Kokoro American Female
        new() { Id = "af_sky", DisplayName = "af_sky (US Female - Clear / Natural)", Category = "Kokoro US Female" },
        new() { Id = "af_bella", DisplayName = "af_bella (US Female - Expressive)", Category = "Kokoro US Female" },
        new() { Id = "af_sarah", DisplayName = "af_sarah (US Female - Smooth)", Category = "Kokoro US Female" },
        new() { Id = "af_nicole", DisplayName = "af_nicole (US Female - Crisp)", Category = "Kokoro US Female" },
        new() { Id = "af_aoede", DisplayName = "af_aoede (US Female - Melodic)", Category = "Kokoro US Female" },
        new() { Id = "af_kore", DisplayName = "af_kore (US Female - Gentle)", Category = "Kokoro US Female" },
        new() { Id = "af_heart", DisplayName = "af_heart (US Female - Warm)", Category = "Kokoro US Female" },

        // Kokoro American Male
        new() { Id = "am_adam", DisplayName = "am_adam (US Male - Narrative)", Category = "Kokoro US Male" },
        new() { Id = "am_michael", DisplayName = "am_michael (US Male - Professional)", Category = "Kokoro US Male" },
        new() { Id = "am_echo", DisplayName = "am_echo (US Male - Balanced)", Category = "Kokoro US Male" },
        new() { Id = "am_eric", DisplayName = "am_eric (US Male - Clear)", Category = "Kokoro US Male" },
        new() { Id = "am_fenrir", DisplayName = "am_fenrir (US Male - Deep)", Category = "Kokoro US Male" },
        new() { Id = "am_liam", DisplayName = "am_liam (US Male - Friendly)", Category = "Kokoro US Male" },
        new() { Id = "am_puck", DisplayName = "am_puck (US Male - Casual)", Category = "Kokoro US Male" },

        // Kokoro British Female
        new() { Id = "bf_emma", DisplayName = "bf_emma (UK Female - Standard)", Category = "Kokoro UK Female" },
        new() { Id = "bf_isabella", DisplayName = "bf_isabella (UK Female - Soft)", Category = "Kokoro UK Female" },
        new() { Id = "bf_alice", DisplayName = "bf_alice (UK Female - Articulate)", Category = "Kokoro UK Female" },
        new() { Id = "bf_lily", DisplayName = "bf_lily (UK Female - Bright)", Category = "Kokoro UK Female" },

        // Kokoro British Male
        new() { Id = "bm_george", DisplayName = "bm_george (UK Male - Classic)", Category = "Kokoro UK Male" },
        new() { Id = "bm_fable", DisplayName = "bm_fable (UK Male - Authoritative)", Category = "Kokoro UK Male" },
        new() { Id = "bm_lewis", DisplayName = "bm_lewis (UK Male - Formal)", Category = "Kokoro UK Male" },
        new() { Id = "bm_daniel", DisplayName = "bm_daniel (UK Male - Conversational)", Category = "Kokoro UK Male" }
    };
}
