//! Launch at login: the HKCU Run key on Windows, a LaunchAgent on macOS, an XDG autostart
//! entry on Linux.

use std::path::PathBuf;

/// The command a shortcut or login item should run to reach this app.
pub fn launcher_command() -> PathBuf {
    if let Some(cmd) = std::env::var_os("DYNAMITE_TTS_COMMAND") {
        return PathBuf::from(cmd);
    }
    std::env::current_exe().unwrap_or_else(|_| PathBuf::from("dynamite-tts"))
}

#[cfg(windows)]
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
#[cfg(windows)]
const RUN_VALUE: &str = "DynamiteTts";

/// Whether a login item exists (made by Settings or by the installer).
#[cfg(windows)]
pub fn is_enabled() -> bool {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;
    RegKey::predef(HKEY_CURRENT_USER).open_subkey(RUN_KEY).and_then(|k| k.get_value::<String, _>(RUN_VALUE)).is_ok()
}

#[cfg(target_os = "macos")]
fn agent_path() -> PathBuf {
    dirs::home_dir().unwrap_or_default().join("Library/LaunchAgents/com.robrab2000.dynamitetts.plist")
}

#[cfg(all(unix, not(target_os = "macos")))]
fn desktop_path() -> PathBuf {
    dirs::config_dir().unwrap_or_default().join("autostart/dynamite-tts.desktop")
}

#[cfg(unix)]
pub fn is_enabled() -> bool {
    #[cfg(target_os = "macos")]
    return agent_path().is_file();
    #[cfg(not(target_os = "macos"))]
    return desktop_path().is_file();
}

#[cfg(windows)]
pub fn apply(enabled: bool) -> std::io::Result<()> {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;
    // The installer writes the same value, so the two never disagree.
    let (run, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(RUN_KEY)?;
    if enabled {
        run.set_value(RUN_VALUE, &format!("\"{}\"", launcher_command().display()))
    } else {
        match run.delete_value(RUN_VALUE) {
            Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        }
    }
}

#[cfg(target_os = "macos")]
pub fn apply(enabled: bool) -> std::io::Result<()> {
    let path = agent_path();
    if !enabled {
        return remove(&path);
    }
    std::fs::create_dir_all(path.parent().expect("has a parent"))?;
    let exe = xml_escape(&launcher_command().display().to_string());
    std::fs::write(
        &path,
        format!(
            r#"<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key>
	<string>com.robrab2000.dynamitetts</string>
	<key>ProgramArguments</key>
	<array>
		<string>{exe}</string>
	</array>
	<key>RunAtLoad</key>
	<true/>
	<key>ProcessType</key>
	<string>Interactive</string>
</dict>
</plist>
"#
        ),
    )
}

#[cfg(target_os = "macos")]
fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

#[cfg(all(unix, not(target_os = "macos")))]
pub fn apply(enabled: bool) -> std::io::Result<()> {
    let path = desktop_path();
    if !enabled {
        return remove(&path);
    }
    std::fs::create_dir_all(path.parent().expect("has a parent"))?;
    std::fs::write(
        &path,
        format!(
            "[Desktop Entry]\nType=Application\nName=Dynamite TTS\nComment=Highlight-to-speech\nExec=\"{}\"\nIcon=dynamite-tts\nTerminal=false\nX-GNOME-Autostart-enabled=true\nX-GNOME-Autostart-Delay=5\n",
            launcher_command().display()
        ),
    )
}

#[cfg(unix)]
fn remove(path: &std::path::Path) -> std::io::Result<()> {
    match std::fs::remove_file(path) {
        Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e),
        _ => Ok(()),
    }
}
