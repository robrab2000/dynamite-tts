using System.IO;
using System.Text.Json;

namespace DynamiteTts.Models;

/// <summary>
/// A voice from Gradium's phonon-voices catalogue. <see cref="Catalog"/> is the embedded snapshot of
/// voices.jsonl at the commit the Phonon runtime pins (same snapshot as the Mac and Linux apps).
/// </summary>
public sealed class PhononVoice
{
    /// <summary>The settings value for the checkpoint's own voice.</summary>
    public const string DefaultId = "default";

    public string VoiceId { get; }
    public string Name { get; }
    public string FileName { get; }
    public string Language { get; }
    public string Locale { get; }
    public string Gender { get; }
    public string Age { get; }
    public string Description { get; }

    public bool IsDefault => string.Equals(VoiceId, DefaultId, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the settings combo shows, e.g. "Easton · EN-US, male".</summary>
    public string DisplayName
    {
        get
        {
            if (IsDefault) return "Default (built into the model)";
            if (!string.IsNullOrWhiteSpace(Locale) && !string.IsNullOrWhiteSpace(Gender))
                return $"{Name} · {Locale.ToUpperInvariant()}, {Gender}";
            if (!string.IsNullOrWhiteSpace(Gender))
                return $"{Name} · {Gender}";
            if (!string.IsNullOrWhiteSpace(Locale))
                return $"{Name} · {Locale.ToUpperInvariant()}";
            return Name;
        }
    }

    public static readonly PhononVoice Default = new(
        DefaultId, DefaultId, "", "en", "en-us", "", "",
        "The voice the checkpoint ships in default-voice.safetensors.");

    public static IReadOnlyList<PhononVoice> Catalog { get; } = LoadCatalog();

    private PhononVoice(string voiceId, string name, string fileName, string language, string locale, string gender, string age, string description)
    {
        VoiceId = voiceId;
        Name = name;
        FileName = fileName;
        Language = language;
        Locale = locale;
        Gender = gender;
        Age = age;
        Description = description;
    }

    /// <summary>Case-insensitive lookup by name or voice id. Null for blank or unknown names.</summary>
    public static PhononVoice? Find(string? nameOrId)
    {
        var wanted = (nameOrId ?? "").Trim();
        if (wanted.Length == 0) return null;
        return Catalog.FirstOrDefault(v => string.Equals(v.VoiceId, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Catalog.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The built-in voice, then every catalogue voice whose language matches.</summary>
    public static IEnumerable<PhononVoice> ForLanguage(string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        yield return Default;
        foreach (var voice in Catalog)
        {
            if (voice.IsDefault) continue;
            if (string.Equals(voice.Language, lang, StringComparison.OrdinalIgnoreCase))
                yield return voice;
        }
    }

    /// <summary>The built-in voice plus every well-formed line, in file order. Bad lines are skipped.</summary>
    public static IReadOnlyList<PhononVoice> ParseCatalog(string jsonl)
    {
        var voices = new List<PhononVoice> { Default };
        foreach (var raw in jsonl.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            VoiceRow? row;
            try
            {
                row = JsonSerializer.Deserialize<VoiceRow>(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (row == null
                || string.IsNullOrWhiteSpace(row.filename)
                || string.IsNullOrWhiteSpace(row.name)
                || string.IsNullOrWhiteSpace(row.voice_id))
            {
                continue;
            }
            voices.Add(new PhononVoice(
                row.voice_id.Trim(),
                row.name.Trim(),
                row.filename.Trim(),
                (row.language ?? "").Trim(),
                (row.locale ?? "").Trim(),
                (row.gender ?? "").Trim(),
                (row.age ?? "").Trim(),
                (row.description ?? "").Trim()));
        }
        return voices;
    }

    private static IReadOnlyList<PhononVoice> LoadCatalog()
    {
        using var stream = typeof(PhononVoice).Assembly.GetManifestResourceStream("phonon-voices.jsonl");
        if (stream == null) return new[] { Default };
        using var reader = new StreamReader(stream);
        var parsed = ParseCatalog(reader.ReadToEnd());
        var rest = parsed.Skip(1)
            .OrderBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase);
        return new[] { Default }.Concat(rest).ToArray();
    }

    private sealed class VoiceRow
    {
        public string? filename { get; set; }
        public string? name { get; set; }
        public string? voice_id { get; set; }
        public string? language { get; set; }
        public string? locale { get; set; }
        public string? gender { get; set; }
        public string? age { get; set; }
        public string? description { get; set; }
    }
}
