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
            RunAtStartup = true,
            PlaySoundOnStop = false
        };

        store.Save(toSave);

        var loaded = store.Load();
        Assert.Equal("OpenMOSS-TTS", loaded.Model);
        Assert.Equal("af_sky", loaded.Voice);
        Assert.Equal(1.25, loaded.Speed);
        Assert.True(loaded.RunAtStartup);
        Assert.False(loaded.PlaySoundOnStop);
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
