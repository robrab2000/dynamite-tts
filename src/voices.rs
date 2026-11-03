//! Gradium's phonon-voices catalogue: the embedded snapshot of voices.jsonl at the commit the
//! Phonon runtime pins. Voice files (about 250 KB each) download the first time one is used.

use std::sync::LazyLock;

use serde::Deserialize;

pub const DEFAULT_ID: &str = "default";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Voice {
    pub voice_id: String,
    pub name: String,
    /// Path of the voice file inside the catalogue repo.
    pub filename: String,
    pub language: String,
    pub locale: String,
    pub gender: String,
}

impl Voice {
    pub fn is_default(&self) -> bool {
        self.voice_id.eq_ignore_ascii_case(DEFAULT_ID)
    }

    /// What Settings shows, e.g. "Easton · EN-US, male".
    pub fn label(&self) -> String {
        if self.is_default() {
            return "Default (built into the model)".into();
        }
        match (self.locale.is_empty(), self.gender.is_empty()) {
            (false, false) => format!("{} · {}, {}", self.name, self.locale.to_uppercase(), self.gender),
            (true, false) => format!("{} · {}", self.name, self.gender),
            (false, true) => format!("{} · {}", self.name, self.locale.to_uppercase()),
            (true, true) => self.name.clone(),
        }
    }
}

#[derive(Deserialize)]
struct Row {
    filename: Option<String>,
    name: Option<String>,
    voice_id: Option<String>,
    language: Option<String>,
    locale: Option<String>,
    gender: Option<String>,
}

fn default_voice() -> Voice {
    Voice {
        voice_id: DEFAULT_ID.into(),
        name: DEFAULT_ID.into(),
        filename: String::new(),
        language: "en".into(),
        locale: String::new(),
        gender: String::new(),
    }
}

/// The built-in voice, then every well-formed line in file order. Bad lines are skipped.
pub fn parse(jsonl: &str) -> Vec<Voice> {
    let mut out = vec![default_voice()];
    for line in jsonl.lines().map(str::trim).filter(|l| !l.is_empty()) {
        let Ok(row) = serde_json::from_str::<Row>(line) else { continue };
        let field = |v: Option<String>| v.unwrap_or_default().trim().to_string();
        let (filename, name, voice_id) = (field(row.filename), field(row.name), field(row.voice_id));
        if filename.is_empty() || name.is_empty() || voice_id.is_empty() {
            continue;
        }
        out.push(Voice {
            voice_id,
            name,
            filename,
            language: field(row.language),
            locale: field(row.locale),
            gender: field(row.gender),
        });
    }
    out
}

/// The default voice, then the catalogue sorted by language and name.
pub static CATALOG: LazyLock<Vec<Voice>> = LazyLock::new(|| {
    let mut voices = parse(include_str!("../assets/phonon-voices.jsonl"));
    voices[1..].sort_by_key(|v| (v.language.to_lowercase(), v.name.to_lowercase()));
    voices
});

/// Case-insensitive lookup by voice id, then by name.
pub fn find(name_or_id: &str) -> Option<&'static Voice> {
    let wanted = name_or_id.trim();
    if wanted.is_empty() {
        return None;
    }
    CATALOG
        .iter()
        .find(|v| v.voice_id.eq_ignore_ascii_case(wanted))
        .or_else(|| CATALOG.iter().find(|v| v.name.eq_ignore_ascii_case(wanted)))
}

/// For Settings: the default voice, the language's own voices, then everything else.
pub fn for_language(language: &str) -> Vec<&'static Voice> {
    let lang = language.trim();
    let (same, other): (Vec<_>, Vec<_>) =
        CATALOG.iter().filter(|v| !v.is_default()).partition(|v| v.language.eq_ignore_ascii_case(lang));
    std::iter::once(&CATALOG[0]).chain(same).chain(other).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_embedded_catalogue_loads() {
        assert!(CATALOG.len() > 90, "{} voices", CATALOG.len());
        assert!(CATALOG[0].is_default());
        assert_eq!(find("easton").unwrap().label(), "Easton · EN-US, male");
        assert_eq!(find("NbpkqMVS3CJeq2j8").unwrap().name, "Zoey");
        assert!(find("nobody").is_none());
    }

    #[test]
    fn bad_lines_are_skipped() {
        let v = parse("{\"name\":\"A\",\"filename\":\"a\",\"voice_id\":\"1\"}\nnot json\n{\"name\":\"B\"}\n");
        assert_eq!(v.len(), 2);
        assert_eq!(v[1].label(), "A");
    }

    #[test]
    fn language_voices_come_first() {
        let fr = for_language("fr");
        assert!(fr[0].is_default());
        assert_eq!(fr[1].language, "fr");
        assert_eq!(fr.len(), CATALOG.len());
    }
}
