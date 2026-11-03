//! The settings model and its JSON file (`paths::settings_file`).
//!
//! Loading is lenient: unknown keys are ignored, bad values fall back to their defaults, and
//! keys are matched without regard to case or underscores, so the files the earlier C# app
//! (`SpeakMode`, `PhononVoice`, ...) and Python app (`speak_mode`, ...) wrote still load.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::hotkey::{self, Hotkey};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum SpeakMode {
    #[default]
    Verbatim,
    Summary,
    Explain,
}

impl SpeakMode {
    pub const ALL: [SpeakMode; 3] = [SpeakMode::Verbatim, SpeakMode::Summary, SpeakMode::Explain];

    pub fn title(self) -> &'static str {
        match self {
            SpeakMode::Verbatim => "Verbatim",
            SpeakMode::Summary => "Summary",
            SpeakMode::Explain => "Explain",
        }
    }

    pub fn next(self) -> SpeakMode {
        match self {
            SpeakMode::Verbatim => SpeakMode::Summary,
            SpeakMode::Summary => SpeakMode::Explain,
            SpeakMode::Explain => SpeakMode::Verbatim,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            SpeakMode::Verbatim => "verbatim",
            SpeakMode::Summary => "summary",
            SpeakMode::Explain => "explain",
        }
    }

    fn parse(s: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|m| m.as_str().eq_ignore_ascii_case(s.trim()))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum ExplainAudience {
    #[default]
    Technical,
    General,
}

impl ExplainAudience {
    pub fn title(self) -> &'static str {
        match self {
            ExplainAudience::Technical => "Technical",
            ExplainAudience::General => "General",
        }
    }

    fn parse(s: &str) -> Option<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "technical" => Some(Self::Technical),
            "general" => Some(Self::General),
            _ => None,
        }
    }
}

pub const DEFAULT_BASE_URL: &str = "http://127.0.0.1:13305";
pub const DEFAULT_CHAT_MODEL: &str = "Llama-3.2-1B-Instruct-GGUF";

#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct Settings {
    pub base_url: String,
    /// "default" = the checkpoint's own voice; otherwise a Phonon catalogue voice name.
    pub phonon_voice: String,
    /// Phonon's text normalization language (en, fr, de, es, pt or none).
    pub phonon_language: String,
    /// Empty = the bundled or downloaded phonon-31m-en; otherwise a checkpoint folder.
    pub phonon_model_dir: String,
    pub speed: f64,
    pub speak_mode: SpeakMode,
    pub speak_hotkey: String,
    pub mode_hotkey: String,
    /// Output device name; empty = the system default.
    pub audio_device: String,
    pub launch_at_login: bool,
    pub show_status: bool,
    pub play_sound_on_stop: bool,
    pub hotkey_stops_speech: bool,
    pub explain_sentence_count: u32,
    pub explain_audience: ExplainAudience,
    pub chat_model: String,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            base_url: DEFAULT_BASE_URL.into(),
            phonon_voice: "default".into(),
            phonon_language: "en".into(),
            phonon_model_dir: String::new(),
            speed: 1.0,
            speak_mode: SpeakMode::Verbatim,
            speak_hotkey: hotkey::DEFAULT_SPEAK.into(),
            mode_hotkey: hotkey::DEFAULT_MODE.into(),
            audio_device: String::new(),
            launch_at_login: false,
            show_status: true,
            play_sound_on_stop: true,
            hotkey_stops_speech: true,
            explain_sentence_count: 3,
            explain_audience: ExplainAudience::Technical,
            chat_model: DEFAULT_CHAT_MODEL.into(),
        }
    }
}

/// Lower-case with underscores removed: `speak_mode`, `SpeakMode` and `speakMode` all match.
fn fold(key: &str) -> String {
    key.chars().filter(|c| *c != '_' && *c != '-').flat_map(char::to_lowercase).collect()
}

struct Fields(Vec<(String, Value)>);

impl Fields {
    fn get(&self, names: &[&str]) -> Option<&Value> {
        names.iter().find_map(|n| {
            let n = fold(n);
            self.0.iter().find(|(k, _)| *k == n).map(|(_, v)| v)
        })
    }

    fn string(&self, names: &[&str]) -> Option<String> {
        self.get(names)?.as_str().map(str::to_string)
    }

    fn bool(&self, names: &[&str]) -> Option<bool> {
        self.get(names)?.as_bool()
    }

    fn number(&self, names: &[&str]) -> Option<f64> {
        self.get(names)?.as_f64()
    }

    /// A shortcut as a string, or the C# app's `{"Modifiers": "Control, Shift", "Key": "S"}`.
    fn hotkey(&self, names: &[&str]) -> Option<String> {
        match self.get(names)? {
            Value::String(s) => Hotkey::parse(s).map(|h| h.to_storage()),
            Value::Object(o) => {
                let get = |k: &str| o.iter().find(|(n, _)| fold(n) == k).map(|(_, v)| v);
                let mods = match get("modifiers")? {
                    Value::String(s) => s.replace(',', "+"),
                    // Flags as a number: Alt = 1, Control = 2, Shift = 4, Win = 8.
                    Value::Number(n) => {
                        let bits = n.as_u64().unwrap_or(0);
                        let names = [(2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win")];
                        names.iter().filter(|(b, _)| bits & b != 0).map(|(_, n)| *n).collect::<Vec<_>>().join("+")
                    }
                    _ => return None,
                };
                let key = match get("key")? {
                    Value::String(s) => s.clone(),
                    _ => return None,
                };
                Hotkey::parse(&format!("{mods}+{key}")).map(|h| h.to_storage())
            }
            _ => None,
        }
    }
}

impl Settings {
    pub fn from_json(value: &Value) -> Settings {
        let empty = Map::new();
        let map = value.as_object().unwrap_or(&empty);
        let f = Fields(map.iter().map(|(k, v)| (fold(k), v.clone())).collect());
        let mut s = Settings::default();

        if let Some(v) = f.string(&["base_url", "baseURL"]) {
            s.base_url = v;
        } else if let Some(v) = f.string(&["ChatEndpoint", "SpeechEndpoint"]) {
            // The C# app stored whole endpoint URLs; keep scheme, host and port.
            if let Ok(u) = url::Url::parse(&v) {
                s.base_url = u.origin().ascii_serialization();
            }
        }
        if let Some(v) = f.string(&["phonon_voice"]) {
            s.phonon_voice = v;
        }
        if let Some(v) = f.string(&["phonon_language"]) {
            s.phonon_language = v;
        }
        if let Some(v) = f.string(&["phonon_model_dir"]) {
            s.phonon_model_dir = v;
        }
        if let Some(v) = f.number(&["speed"]) {
            s.speed = v;
        }
        if let Some(m) = f.string(&["speak_mode"]).and_then(|v| SpeakMode::parse(&v)) {
            s.speak_mode = m;
        }
        if let Some(h) = f.hotkey(&["speak_hotkey", "Hotkey"]) {
            s.speak_hotkey = h;
        }
        if let Some(h) = f.hotkey(&["mode_hotkey", "ModeToggleHotkey"]) {
            s.mode_hotkey = h;
        }
        // Only the name this app stores: the C# app saved WASAPI endpoint ids and the Swift app
        // CoreAudio UIDs, which never match a device name, so those start on the default output.
        if let Some(v) = f.string(&["audio_device"]) {
            s.audio_device = v;
        }
        if let Some(v) = f.bool(&["launch_at_login", "RunAtStartup"]) {
            s.launch_at_login = v;
        }
        if let Some(v) = f.bool(&["show_status", "show_status_notifications", "ShowStatusBubble", "showStatusPanel"]) {
            s.show_status = v;
        }
        if let Some(v) = f.bool(&["play_sound_on_stop"]) {
            s.play_sound_on_stop = v;
        }
        if let Some(v) = f.bool(&["hotkey_stops_speech"]) {
            s.hotkey_stops_speech = v;
        }
        if let Some(v) = f.number(&["explain_sentence_count"]) {
            s.explain_sentence_count = v.clamp(0.0, 100.0) as u32;
        }
        if let Some(a) = f.string(&["explain_audience"]).and_then(|v| ExplainAudience::parse(&v)) {
            s.explain_audience = a;
        }
        if let Some(v) = f.string(&["chat_model", "SummaryModel"]).filter(|v| !v.trim().is_empty()) {
            s.chat_model = v;
        }
        s.normalized()
    }

    pub fn normalized(mut self) -> Settings {
        self.explain_sentence_count = self.explain_sentence_count.clamp(1, 8);
        self.speed = if self.speed.is_finite() { self.speed.clamp(0.25, 4.0) } else { 1.0 };
        self.phonon_language = match self.phonon_language.trim().to_ascii_lowercase() {
            l if l.is_empty() => "en".into(),
            l => l,
        };
        self.phonon_voice = match self.phonon_voice.trim() {
            "" => "default".into(),
            v => v.into(),
        };
        self.phonon_model_dir = self.phonon_model_dir.trim().into();
        self.base_url = match self.base_url.trim().trim_end_matches('/') {
            "" => DEFAULT_BASE_URL.into(),
            u => u.into(),
        };
        self.chat_model = match self.chat_model.trim() {
            "" => DEFAULT_CHAT_MODEL.into(),
            v => v.into(),
        };
        self.speak_hotkey =
            Hotkey::parse(&self.speak_hotkey).map_or_else(|| hotkey::DEFAULT_SPEAK.into(), |h| h.to_storage());
        self.mode_hotkey =
            Hotkey::parse(&self.mode_hotkey).map_or_else(|| hotkey::DEFAULT_MODE.into(), |h| h.to_storage());
        self
    }

    pub fn speak_hotkey(&self) -> Hotkey {
        Hotkey::parse(&self.speak_hotkey).expect("normalized")
    }

    pub fn mode_hotkey(&self) -> Hotkey {
        Hotkey::parse(&self.mode_hotkey).expect("normalized")
    }
}

/// Reads and writes one settings file.
pub struct Store {
    path: PathBuf,
}

impl Store {
    pub fn new(path: PathBuf) -> Store {
        Store { path }
    }

    pub fn default_location() -> Store {
        Store::new(crate::paths::settings_file())
    }

    #[cfg(test)]
    pub fn path(&self) -> &std::path::Path {
        &self.path
    }

    pub fn load(&self) -> Settings {
        std::fs::read_to_string(&self.path)
            .ok()
            .and_then(|text| serde_json::from_str::<Value>(text.trim_start_matches('\u{feff}')).ok())
            .map(|v| Settings::from_json(&v))
            .unwrap_or_default()
    }

    /// Writes through a temporary file so a crash never leaves half a settings file.
    pub fn save(&self, settings: &Settings) -> std::io::Result<()> {
        if let Some(dir) = self.path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = self.path.with_extension("json.tmp");
        let mut text = serde_json::to_string_pretty(settings).map_err(std::io::Error::other)?;
        text.push('\n');
        std::fs::write(&tmp, text)?;
        std::fs::rename(&tmp, &self.path)
    }
}

pub const PHONON_LANGUAGES: &[(&str, &str)] = &[
    ("en", "English"),
    ("fr", "French (needs a Phonon 40M model)"),
    ("de", "German (needs a Phonon 40M model)"),
    ("es", "Spanish (needs a Phonon 40M model)"),
    ("pt", "Portuguese (needs a Phonon 40M model)"),
    ("none", "None (read text as written)"),
];

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn defaults_round_trip() {
        let s = Settings::default();
        let v = serde_json::to_value(&s).unwrap();
        assert_eq!(Settings::from_json(&v), s);
    }

    #[test]
    fn values_are_clamped() {
        let s = Settings::from_json(&json!({
            "speed": 9.0, "explain_sentence_count": 0, "phonon_language": " FR ",
            "phonon_voice": "  ", "base_url": "http://host:1/ ", "speak_hotkey": "nonsense"
        }));
        assert_eq!(s.speed, 4.0);
        assert_eq!(s.explain_sentence_count, 1);
        assert_eq!(s.phonon_language, "fr");
        assert_eq!(s.phonon_voice, "default");
        assert_eq!(s.base_url, "http://host:1");
        assert_eq!(s.speak_hotkey, "Ctrl+Shift+S");
    }

    #[test]
    fn bad_values_fall_back_to_defaults() {
        let s = Settings::from_json(&json!({"speak_mode": "shout", "engine": 3, "speed": "fast"}));
        assert_eq!(s.speak_mode, SpeakMode::Verbatim);
        assert_eq!(s.speed, 1.0);
    }

    #[test]
    fn loads_the_linux_python_settings() {
        let s = Settings::from_json(&json!({
            "base_url": "http://127.0.0.1:13305", "engine": "kokoro", "voice": "bm_george",
            "speak_mode": "summary", "speak_hotkey": "<Control><Alt>r", "mode_hotkey": "<Control><Shift>m",
            "show_status_notifications": false, "explain_audience": "general"
        }));
        assert_eq!(s.speak_mode, SpeakMode::Summary);
        assert_eq!(s.speak_hotkey, "Ctrl+Alt+R");
        assert!(!s.show_status);
        assert_eq!(s.explain_audience, ExplainAudience::General);
    }

    #[test]
    fn loads_the_windows_csharp_settings() {
        let s = Settings::from_json(&json!({
            "SpeechEndpoint": "http://localhost:13305/api/v1/audio/speech",
            "Voice": "af_sky", "Speed": 1.25,
            "Hotkey": {"Modifiers": "Control, Alt", "Key": "D5"},
            "ModeToggleHotkey": {"Modifiers": 6, "Key": "M"},
            "SpeakMode": "Explain", "SummaryModel": "Qwen2.5-0.5B-Instruct",
            "RunAtStartup": true, "ShowStatusBubble": false, "EngineMode": "DirectML",
            "PhononVoice": "Easton", "ExplainSentenceCount": 12,
            "AudioDeviceId": "{0.0.0.00000000}.{e5582329-e8ab-424a-961e-152ddf011e76}"
        }));
        assert_eq!(s.base_url, "http://localhost:13305");
        assert_eq!(s.speed, 1.25);
        assert_eq!(s.speak_hotkey, "Ctrl+Alt+5");
        assert_eq!(s.mode_hotkey, "Ctrl+Shift+M");
        assert_eq!(s.speak_mode, SpeakMode::Explain);
        assert_eq!(s.chat_model, "Qwen2.5-0.5B-Instruct");
        assert!(s.launch_at_login);
        assert!(!s.show_status);
        assert_eq!(s.phonon_voice, "Easton");
        assert_eq!(s.audio_device, "", "an endpoint id is not a device name");
        assert_eq!(s.explain_sentence_count, 8);
    }

    #[test]
    fn store_saves_atomically_and_reloads() {
        let dir = std::env::temp_dir().join(format!("dt-settings-{}", std::process::id()));
        let store = Store::new(dir.join("settings.json"));
        assert_eq!(store.load(), Settings::default(), "a missing file gives the defaults");
        let s = Settings { speak_mode: SpeakMode::Explain, audio_device: "Speakers".into(), ..Default::default() };
        store.save(&s).unwrap();
        assert_eq!(store.load(), s);
        assert!(!dir.join("settings.json.tmp").exists());
        std::fs::write(store.path(), "{ not json").unwrap();
        assert_eq!(store.load(), Settings::default());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn modes_cycle() {
        assert_eq!(SpeakMode::Verbatim.next(), SpeakMode::Summary);
        assert_eq!(SpeakMode::Summary.next(), SpeakMode::Explain);
        assert_eq!(SpeakMode::Explain.next(), SpeakMode::Verbatim);
    }
}
