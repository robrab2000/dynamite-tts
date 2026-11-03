//! Keyboard shortcuts as stored in settings ("Ctrl+Shift+S"), with conversions to the
//! `global-hotkey` crate and to GTK accelerators (GNOME custom keybindings).

use std::fmt;

#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct Hotkey {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
    /// Win on Windows, Cmd on macOS, Super on Linux.
    pub meta: bool,
    /// Canonical key name: "A"-"Z", "0"-"9", "F1"-"F24", "Space", "Tab", "Enter", ...
    pub key: String,
}

pub const DEFAULT_SPEAK: &str = "Ctrl+Shift+S";
pub const DEFAULT_MODE: &str = "Ctrl+Shift+M";

/// Named keys besides letters, digits and F-keys, with their GTK accelerator names.
const NAMED: &[(&str, &str)] = &[
    ("Space", "space"),
    ("Tab", "Tab"),
    ("Enter", "Return"),
    ("Escape", "Escape"),
    ("Backspace", "BackSpace"),
    ("Delete", "Delete"),
    ("Insert", "Insert"),
    ("Home", "Home"),
    ("End", "End"),
    ("PageUp", "Page_Up"),
    ("PageDown", "Page_Down"),
    ("Up", "Up"),
    ("Down", "Down"),
    ("Left", "Left"),
    ("Right", "Right"),
    ("Minus", "minus"),
    ("Equal", "equal"),
    ("Comma", "comma"),
    ("Period", "period"),
    ("Slash", "slash"),
    ("Backslash", "backslash"),
    ("Semicolon", "semicolon"),
    ("Quote", "apostrophe"),
    ("BracketLeft", "bracketleft"),
    ("BracketRight", "bracketright"),
    ("Backquote", "grave"),
];

/// Canonical name for a key as typed by a user, written by an older app, or reported by egui.
pub fn canonical_key(raw: &str) -> Option<String> {
    let k = raw.trim();
    if k.is_empty() {
        return None;
    }
    if k.chars().count() == 1 {
        let c = k.chars().next()?;
        if c.is_ascii_alphanumeric() {
            return Some(c.to_ascii_uppercase().to_string());
        }
        let named = match c {
            ' ' => "Space",
            '-' => "Minus",
            '=' => "Equal",
            ',' => "Comma",
            '.' => "Period",
            '/' => "Slash",
            '\\' => "Backslash",
            ';' => "Semicolon",
            '\'' => "Quote",
            '[' => "BracketLeft",
            ']' => "BracketRight",
            '`' => "Backquote",
            _ => return None,
        };
        return Some(named.into());
    }
    let lower = k.to_ascii_lowercase();
    // WPF's Key enum names digits D0-D9 and the number pad NumPad0-9.
    if let Some(d) = lower.strip_prefix("numpad").or_else(|| lower.strip_prefix('d')) {
        if d.len() == 1 && d.chars().all(|c| c.is_ascii_digit()) {
            return Some(d.into());
        }
    }
    if let Some(n) = lower.strip_prefix('f') {
        if let Ok(n) = n.parse::<u8>() {
            if (1..=24).contains(&n) {
                return Some(format!("F{n}"));
            }
        }
    }
    let aliases: &[(&str, &str)] = &[
        ("return", "Enter"),
        ("esc", "Escape"),
        ("back", "Backspace"),
        ("del", "Delete"),
        ("ins", "Insert"),
        ("pgup", "PageUp"),
        ("prior", "PageUp"),
        ("pgdn", "PageDown"),
        ("next", "PageDown"),
        ("arrowup", "Up"),
        ("arrowdown", "Down"),
        ("arrowleft", "Left"),
        ("arrowright", "Right"),
        ("apostrophe", "Quote"),
        ("grave", "Backquote"),
        ("oemminus", "Minus"),
        ("oemplus", "Equal"),
        ("oemcomma", "Comma"),
        ("oemperiod", "Period"),
    ];
    if let Some((_, name)) = aliases.iter().find(|(a, _)| *a == lower) {
        return Some((*name).into());
    }
    NAMED
        .iter()
        .find(|(name, gtk)| name.eq_ignore_ascii_case(k) || gtk.eq_ignore_ascii_case(k))
        .map(|(name, _)| (*name).into())
}

impl Hotkey {
    /// Parses "Ctrl+Shift+S" (also "Control", "Cmd", "Win", "Super", "Option") or a GTK
    /// accelerator ("<Control><Shift>s"). None when there is no key or no modifier.
    pub fn parse(text: &str) -> Option<Hotkey> {
        let mut hk = Hotkey::default();
        let mut rest = text.trim();
        // GTK form: leading <Modifier> groups.
        while let Some(stripped) = rest.strip_prefix('<') {
            let end = stripped.find('>')?;
            hk.set_modifier(&stripped[..end]).then_some(())?;
            rest = &stripped[end + 1..];
        }
        let parts: Vec<&str> = rest.split('+').map(str::trim).collect();
        // "Ctrl++" would split into empty parts; a plus key is not supported.
        let (key, mods) = parts.split_last()?;
        for m in mods {
            if !hk.set_modifier(m) {
                return None;
            }
        }
        hk.key = canonical_key(key)?;
        hk.has_modifier().then_some(hk)
    }

    fn set_modifier(&mut self, name: &str) -> bool {
        match name.to_ascii_lowercase().as_str() {
            "ctrl" | "control" | "primary" | "ctl" => self.ctrl = true,
            "alt" | "option" | "opt" | "menu" => self.alt = true,
            "shift" => self.shift = true,
            "win" | "windows" | "super" | "meta" | "cmd" | "command" | "hyper" | "logo" => self.meta = true,
            _ => return false,
        }
        true
    }

    pub fn has_modifier(&self) -> bool {
        self.ctrl || self.alt || self.shift || self.meta
    }

    /// The storage form, also what the settings window shows on Windows and Linux.
    pub fn to_storage(&self) -> String {
        let mut parts = Vec::new();
        if self.ctrl {
            parts.push("Ctrl");
        }
        if self.alt {
            parts.push("Alt");
        }
        if self.shift {
            parts.push("Shift");
        }
        if self.meta {
            parts.push("Super");
        }
        parts.push(&self.key);
        parts.join("+")
    }

    /// What the user sees, with this platform's name for the meta and alt keys.
    pub fn display(&self) -> String {
        let meta = if cfg!(target_os = "macos") {
            "Cmd"
        } else if cfg!(windows) {
            "Win"
        } else {
            "Super"
        };
        let alt = if cfg!(target_os = "macos") { "Opt" } else { "Alt" };
        let mut parts = Vec::new();
        if self.ctrl {
            parts.push("Ctrl");
        }
        if self.alt {
            parts.push(alt);
        }
        if self.shift {
            parts.push("Shift");
        }
        if self.meta {
            parts.push(meta);
        }
        parts.push(&self.key);
        parts.join("+")
    }

    /// GTK accelerator syntax, as GNOME's custom keybindings store it: "<Control><Shift>s".
    pub fn to_gtk_accelerator(&self) -> String {
        let mut out = String::new();
        if self.ctrl {
            out.push_str("<Control>");
        }
        if self.alt {
            out.push_str("<Alt>");
        }
        if self.shift {
            out.push_str("<Shift>");
        }
        if self.meta {
            out.push_str("<Super>");
        }
        let key = &self.key;
        if key.len() == 1 {
            out.push_str(&key.to_ascii_lowercase());
        } else if let Some((_, gtk)) = NAMED.iter().find(|(n, _)| n == key) {
            out.push_str(gtk);
        } else {
            out.push_str(key);
        }
        out
    }

    pub fn to_global(&self) -> Option<global_hotkey::hotkey::HotKey> {
        use global_hotkey::hotkey::{HotKey, Modifiers};
        let mut mods = Modifiers::empty();
        if self.ctrl {
            mods |= Modifiers::CONTROL;
        }
        if self.alt {
            mods |= Modifiers::ALT;
        }
        if self.shift {
            mods |= Modifiers::SHIFT;
        }
        if self.meta {
            mods |= Modifiers::META;
        }
        Some(HotKey::new(Some(mods), key_code(&self.key)?))
    }
}

impl fmt::Display for Hotkey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.display())
    }
}

fn key_code(key: &str) -> Option<global_hotkey::hotkey::Code> {
    use global_hotkey::hotkey::Code;
    // Code's FromStr takes W3C names: "KeyS", "Digit1", "F5", "Space", "ArrowUp".
    let w3c = if key.len() == 1 && key.as_bytes()[0].is_ascii_alphabetic() {
        format!("Key{key}")
    } else if key.len() == 1 && key.as_bytes()[0].is_ascii_digit() {
        format!("Digit{key}")
    } else {
        match key {
            "Up" | "Down" | "Left" | "Right" => format!("Arrow{key}"),
            other => other.to_string(),
        }
    };
    w3c.parse::<Code>().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_storage_form() {
        let hk = Hotkey::parse("Ctrl+Shift+S").unwrap();
        assert!(hk.ctrl && hk.shift && !hk.alt && !hk.meta);
        assert_eq!(hk.key, "S");
        assert_eq!(hk.to_storage(), "Ctrl+Shift+S");
    }

    #[test]
    fn parses_gtk_accelerators_from_the_linux_app() {
        let hk = Hotkey::parse("<Control><Shift>m").unwrap();
        assert_eq!(hk.to_storage(), "Ctrl+Shift+M");
        assert_eq!(hk.to_gtk_accelerator(), "<Control><Shift>m");
        assert_eq!(Hotkey::parse("<Primary><Alt>F5").unwrap().to_storage(), "Ctrl+Alt+F5");
    }

    #[test]
    fn accepts_platform_modifier_names() {
        assert!(Hotkey::parse("Cmd+Option+1").unwrap().meta);
        assert!(Hotkey::parse("win+alt+space").unwrap().alt);
        assert_eq!(Hotkey::parse("Control+Shift+D5").unwrap().key, "5");
        assert_eq!(Hotkey::parse("ctrl+pgdn").unwrap().key, "PageDown");
    }

    #[test]
    fn rejects_unusable_shortcuts() {
        assert_eq!(Hotkey::parse(""), None);
        assert_eq!(Hotkey::parse("S"), None, "a bare key is not a global shortcut");
        assert_eq!(Hotkey::parse("Ctrl+Shift"), None);
        assert_eq!(Hotkey::parse("Ctrl+Bogus+S"), None);
        assert_eq!(Hotkey::parse("<Control>"), None);
    }

    #[test]
    fn named_keys_round_trip_through_gtk() {
        let hk = Hotkey::parse("Ctrl+Alt+PageUp").unwrap();
        assert_eq!(hk.to_gtk_accelerator(), "<Control><Alt>Page_Up");
        assert_eq!(Hotkey::parse(&hk.to_gtk_accelerator()).unwrap(), hk);
    }

    #[test]
    fn converts_to_global_hotkeys() {
        for text in ["Ctrl+Shift+S", "Alt+F12", "Super+1", "Ctrl+Space", "Ctrl+Up", "Ctrl+Slash"] {
            assert!(Hotkey::parse(text).unwrap().to_global().is_some(), "{text}");
        }
    }
}
