using System;
using System.IO;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _tempPath;

    public AppSettingsStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"dynamite_test_{Guid.NewGuid()}.json");
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsDefaultSettings()
    {
        var store = new AppSettingsStore(_tempPath);
        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal("kokoro-v1", settings.Model);
        Assert.Equal("coral", settings.Voice);
        Assert.True(settings.PlaySoundOnStop);
    }

    [Fact]
    public void SaveAndLoad_PersistsValuesCorrectly()
    {
        var store = new AppSettingsStore(_tempPath);
        var toSave = new AppSettings
        {
            SpeechEndpoint = "http://localhost:8000/api/v1/audio/speech",
            Model = "OpenMOSS-TTS",
            Voice = "af_sky",
            Speed = 1.25,
            ChatModel = "Qwen2.5-0.5B-Instruct",
            ExplainSentenceCount = 4,
            ExplainAudience = ExplainAudience.General,
            SpeakMode = SpeakMode.Explain,
            RunAtStartup = true,
            PlaySoundOnStop = false
        };

        store.Save(toSave);

        var loaded = store.Load();
        Assert.Equal("OpenMOSS-TTS", loaded.Model);
        Assert.Equal("af_sky", loaded.Voice);
        Assert.Equal(1.25, loaded.Speed);
        Assert.Equal("Qwen2.5-0.5B-Instruct", loaded.ChatModel);
        Assert.Equal(4, loaded.ExplainSentenceCount);
        Assert.Equal(ExplainAudience.General, loaded.ExplainAudience);
        Assert.Equal(SpeakMode.Explain, loaded.SpeakMode);
        Assert.True(loaded.RunAtStartup);
        Assert.False(loaded.PlaySoundOnStop);
        Assert.DoesNotContain("SummaryModel", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void Load_MigratesLegacySummaryModelToChatModel()
    {
        File.WriteAllText(_tempPath, """
            {
              "SpeechEndpoint": "http://localhost:13305/api/v1/audio/speech",
              "Model": "kokoro-v1",
              "Voice": "coral",
              "Speed": 1.0,
              "SummaryModel": "legacy-chat-model",
              "SpeakMode": "Summary"
            }
            """);

        var store = new AppSettingsStore(_tempPath);
        var loaded = store.Current;
        Assert.Equal("legacy-chat-model", loaded.ChatModel);

        store.Save(loaded);
        var savedJson = File.ReadAllText(_tempPath);
        Assert.Contains("ChatModel", savedJson);
        Assert.Contains("legacy-chat-model", savedJson);
        Assert.DoesNotContain("SummaryModel", savedJson);
    }

    [Fact]
    public void Save_ClampsExplainSentenceCount()
    {
        var store = new AppSettingsStore(_tempPath);
        var settings = store.Current;
        settings.ExplainSentenceCount = 99;
        store.Save(settings);
        Assert.Equal(8, store.Current.ExplainSentenceCount);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch { }
    }
}
