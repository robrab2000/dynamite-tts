using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class PhononTtsServiceTests
{
    [Fact]
    public void DurationDeltaForSpeed_IsZeroAtNormalSpeed_NegativeWhenFaster_AndClamped()
    {
        Assert.Equal(0f, PhononTtsService.DurationDeltaForSpeed(1.0), 5);
        Assert.True(PhononTtsService.DurationDeltaForSpeed(1.2) < 0);
        Assert.True(PhononTtsService.DurationDeltaForSpeed(0.8) > 0);

        // Monotonic: faster speech, smaller (more negative) delta.
        var deltas = new[] { 0.5, 0.8, 1.0, 1.25, 1.5, 2.0 }.Select(PhononTtsService.DurationDeltaForSpeed).ToArray();
        for (int i = 1; i < deltas.Length; i++)
            Assert.True(deltas[i] <= deltas[i - 1], $"delta not monotonic at index {i}");

        // The model only speaks reliably for small magnitudes, so the slider's extremes are clamped.
        Assert.Equal((float)-PhononTtsService.MaxDurationDelta, PhononTtsService.DurationDeltaForSpeed(4.0));
        Assert.Equal((float)PhononTtsService.MaxDurationDelta, PhononTtsService.DurationDeltaForSpeed(0.25));

        // Garbage in, default rate out.
        Assert.Equal(0f, PhononTtsService.DurationDeltaForSpeed(0));
        Assert.Equal(0f, PhononTtsService.DurationDeltaForSpeed(double.NaN));
    }

    [Fact]
    public void VoiceCatalog_IsEmbedded_AndFindsVoicesByNameOrId()
    {
        var catalog = PhononVoice.Catalog;
        Assert.True(catalog.Count > 50, $"expected the embedded catalogue, got {catalog.Count} voices");
        Assert.True(catalog[0].IsDefault);

        var easton = PhononVoice.Find("easton");
        Assert.NotNull(easton);
        Assert.Equal("Easton", easton!.Name);
        Assert.Equal("en", easton.Language);
        Assert.Equal("voices/LMDIYE0Oa377Qh6Z.safetensors", easton.FileName);
        Assert.Same(easton, PhononVoice.Find("LMDIYE0Oa377Qh6Z"));

        Assert.Same(PhononVoice.Default, PhononVoice.Find("default"));
        Assert.Null(PhononVoice.Find("no-such-voice"));
        Assert.Null(PhononVoice.Find(""));

        var english = PhononVoice.ForLanguage("en").ToList();
        Assert.True(english[0].IsDefault);
        Assert.All(english.Skip(1), v => Assert.Equal("en", v.Language));
        Assert.Contains(english, v => v.Name == "Easton");
        Assert.True(PhononVoice.ForLanguage("fr").Count() > 1);
    }

    [Fact]
    public void ParseCatalog_SkipsMalformedLines()
    {
        var jsonl = "{\"filename\": \"voices/A.safetensors\", \"name\": \"Alpha\", \"voice_id\": \"A\", \"language\": \"en\"}\n" +
                    "not json\n" +
                    "{\"name\": \"NoFile\"}\n" +
                    "\n" +
                    "{\"filename\": \"voices/B.safetensors\", \"name\": \"Bravo\", \"voice_id\": \"B\", \"language\": \"fr\", \"gender\": \"male\"}\n";
        var voices = PhononVoice.ParseCatalog(jsonl);
        Assert.Equal(3, voices.Count); // default + 2
        Assert.Equal("Alpha", voices[1].Name);
        Assert.Equal("Bravo", voices[2].Name);
        Assert.Contains("male", voices[2].DisplayName);
    }

    [Fact]
    public void ResolveModelDirectory_PrefersCustomCheckpoint_AndRejectsFoldersWithoutOne()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dynamite-phonon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => PhononTtsService.ResolveModelDirectory(tmp));

            File.WriteAllText(Path.Combine(tmp, "config.json"), "{}");
            File.WriteAllText(Path.Combine(tmp, "tokenizer.json"), "{}");
            File.WriteAllBytes(Path.Combine(tmp, "model.q8.gguf"), new byte[16]);
            Assert.True(PhononTtsService.IsCheckpoint(tmp));
            Assert.Equal(tmp, PhononTtsService.ResolveModelDirectory(tmp));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void NormalizeLanguage_DefaultsToEnglish()
    {
        Assert.Equal("en", PhononTtsService.NormalizeLanguage(null));
        Assert.Equal("en", PhononTtsService.NormalizeLanguage("  "));
        Assert.Equal("fr", PhononTtsService.NormalizeLanguage(" FR "));
    }

    [Fact]
    public async Task GenerateSpeechWavAsync_EmptyText_ReturnsEmptyWithoutLoading()
    {
        using var service = new PhononTtsService();
        var wav = await service.GenerateSpeechWavAsync("   ", "default", 1.0, null, "en");
        Assert.Empty(wav);
        Assert.False(service.IsLoaded);
        Assert.Equal(PhononEngineState.NotInitialized, service.State);
    }

    [Fact]
    public void AppSettings_RoundTripsPhononFields_AndEngineFlags()
    {
        var settings = new AppSettings
        {
            EngineMode = AppSettings.EnginePhonon,
            PhononVoice = "Easton",
            PhononModelDir = @"C:\models\phonon-40m-multilingual",
            PhononLanguage = "fr"
        };
        Assert.True(settings.IsPhononEngine);
        Assert.True(settings.IsLocalEngine);
        Assert.False(settings.IsKokoroEngine);

        var clone = settings.Clone();
        Assert.Equal("Easton", clone.PhononVoice);
        Assert.Equal(settings.PhononModelDir, clone.PhononModelDir);
        Assert.Equal("fr", clone.PhononLanguage);

        var defaults = new AppSettings();
        Assert.Equal(AppSettings.DefaultEngineMode, defaults.EngineMode);
        Assert.Equal(System.Runtime.Intrinsics.X86.Avx2.IsSupported, defaults.IsPhononEngine);
        Assert.Equal(PhononVoice.DefaultId, defaults.PhononVoice);
        Assert.Equal("en", defaults.PhononLanguage);
    }
}
