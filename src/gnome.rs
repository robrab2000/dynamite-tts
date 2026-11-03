//! GNOME custom keyboard shortcuts.
//!
//! Wayland does not let a background app grab keys, so on GNOME the app registers two custom
//! shortcuts (Settings → Keyboard → Custom Shortcuts) that run `dynamite-tts speak` and
//! `dynamite-tts toggle-mode`; those commands reach the running app over its control socket.
//! Other desktops bind the same two commands by hand.

use std::process::{Command, Stdio};

use crate::hotkey::Hotkey;
use crate::log;

const MEDIA_KEYS: &str = "org.gnome.settings-daemon.plugins.media-keys";
const CUSTOM: &str = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";
const BASE: &str = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/";
const SPEAK: &str = "dynamite-tts-speak/";
const MODE: &str = "dynamite-tts-mode/";

fn gsettings(args: &[&str]) -> Option<String> {
    let out = Command::new("gsettings").args(args).stdin(Stdio::null()).stderr(Stdio::null()).output().ok()?;
    out.status.success().then(|| String::from_utf8_lossy(&out.stdout).trim().to_string())
}

/// True when GNOME's media-keys schema (and so custom shortcuts) is available.
pub fn available() -> bool {
    gsettings(&["list-keys", MEDIA_KEYS]).is_some_and(|keys| keys.lines().any(|k| k == "custom-keybindings"))
}

/// Parses gsettings' `['/a/', '/b/']` (or `@as []`).
pub fn parse_list(text: &str) -> Vec<String> {
    let inner = text.trim().trim_start_matches("@as").trim().trim_start_matches('[').trim_end_matches(']');
    inner
        .split(',')
        .map(|s| s.trim().trim_matches('\'').trim_matches('"').to_string())
        .filter(|s| !s.is_empty())
        .collect()
}

pub fn format_list(items: &[String]) -> String {
    let quoted: Vec<String> = items.iter().map(|i| format!("'{i}'")).collect();
    format!("[{}]", quoted.join(", "))
}

/// GVariant string literal.
fn quote(s: &str) -> String {
    format!("'{}'", s.replace('\\', "\\\\").replace('\'', "\\'"))
}

fn write_entry(path: &str, name: &str, command: &str, binding: &str) {
    let schema = format!("{CUSTOM}:{path}");
    for (key, value) in [("name", name), ("command", command), ("binding", binding)] {
        let wanted = quote(value);
        if gsettings(&["get", &schema, key]).as_deref() != Some(wanted.as_str()) {
            let _ = gsettings(&["set", &schema, key, &wanted]);
        }
    }
}

/// Points the two shortcuts at `launcher` (quoted if needed) with the given bindings.
pub fn bind(launcher: &str, speak: &Hotkey, mode: &Hotkey) {
    let exe = if launcher.contains(' ') { format!("\"{launcher}\"") } else { launcher.to_string() };
    let speak_path = format!("{BASE}{SPEAK}");
    let mode_path = format!("{BASE}{MODE}");
    write_entry(&speak_path, "Dynamite TTS: Speak selection", &format!("{exe} speak"), &speak.to_gtk_accelerator());
    write_entry(&mode_path, "Dynamite TTS: Toggle mode", &format!("{exe} toggle-mode"), &mode.to_gtk_accelerator());
    let mut paths = gsettings(&["get", MEDIA_KEYS, "custom-keybindings"]).map(|t| parse_list(&t)).unwrap_or_default();
    let before = paths.len();
    for p in [&speak_path, &mode_path] {
        if !paths.contains(p) {
            paths.push(p.clone());
        }
    }
    if paths.len() != before {
        let _ = gsettings(&["set", MEDIA_KEYS, "custom-keybindings", &format_list(&paths)]);
    }
    log!("hotkeys bound speak={} mode={} via GNOME custom keybindings", speak.to_storage(), mode.to_storage());
}

/// Removes both shortcuts (used by `dynamite-tts uninstall-shortcuts`).
pub fn unbind() {
    let speak_path = format!("{BASE}{SPEAK}");
    let mode_path = format!("{BASE}{MODE}");
    if let Some(list) = gsettings(&["get", MEDIA_KEYS, "custom-keybindings"]) {
        let paths: Vec<String> =
            parse_list(&list).into_iter().filter(|p| *p != speak_path && *p != mode_path).collect();
        let _ = gsettings(&["set", MEDIA_KEYS, "custom-keybindings", &format_list(&paths)]);
    }
    for path in [speak_path, mode_path] {
        let schema = format!("{CUSTOM}:{path}");
        for key in ["name", "command", "binding"] {
            let _ = gsettings(&["reset", &schema, key]);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_formats_gsettings_lists() {
        assert!(parse_list("@as []").is_empty());
        let items = parse_list("['/a/', '/b/']");
        assert_eq!(items, vec!["/a/", "/b/"]);
        assert_eq!(format_list(&items), "['/a/', '/b/']");
        assert_eq!(quote("it's"), "'it\\'s'");
    }
}
