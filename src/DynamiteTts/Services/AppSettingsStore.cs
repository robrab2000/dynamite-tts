using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

public class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private AppSettings _current;

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current.Clone();
            }
        }
    }

    public AppSettingsStore(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "DynamiteTts");
            _filePath = Path.Combine(dir, "settings.json");
        }

        _current = LoadInternal();
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            _current = LoadInternal();
            return _current.Clone();
        }
    }

    private AppSettings LoadInternal()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }

            _current = settings.Clone();
        }

        SettingsChanged?.Invoke(_current.Clone());
    }
}
