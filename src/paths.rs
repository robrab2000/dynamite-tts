//! Where settings, models, logs and the control socket live on each platform.
//!
//! The folders keep the names the earlier per-platform apps used, so an upgrade finds the
//! downloaded Phonon model and (on Windows and Linux) the old settings file.

use std::path::{Path, PathBuf};

#[cfg(windows)]
const APP_DIR: &str = "DynamiteTts";
#[cfg(target_os = "macos")]
const APP_DIR: &str = "DynamiteTTS";
#[cfg(all(unix, not(target_os = "macos")))]
const APP_DIR: &str = "dynamite-tts";

fn home() -> PathBuf {
    dirs::home_dir().unwrap_or_else(|| PathBuf::from("."))
}

/// settings.json: %AppData%\DynamiteTts, ~/Library/Application Support/DynamiteTTS, ~/.config/dynamite-tts.
pub fn config_dir() -> PathBuf {
    dirs::config_dir().unwrap_or_else(home).join(APP_DIR)
}

/// Models, the instance lock and Lemonade's log: %LocalAppData%\DynamiteTts,
/// ~/Library/Application Support/DynamiteTTS, ~/.local/share/dynamite-tts.
pub fn data_dir() -> PathBuf {
    dirs::data_local_dir().unwrap_or_else(home).join(APP_DIR)
}

/// The diagnostics log's folder.
pub fn log_dir() -> PathBuf {
    #[cfg(target_os = "macos")]
    {
        home().join("Library/Logs").join(APP_DIR)
    }
    #[cfg(windows)]
    {
        data_dir().join("logs")
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        dirs::state_dir().unwrap_or_else(|| home().join(".local/state")).join(APP_DIR)
    }
}

pub fn settings_file() -> PathBuf {
    config_dir().join("settings.json")
}

pub fn log_file() -> PathBuf {
    log_dir().join("dynamite.log")
}

pub fn models_dir() -> PathBuf {
    data_dir().join("models")
}

/// The folder holding the running executable (and, in a release, `models/phonon-31m-en`).
pub fn exe_dir() -> Option<PathBuf> {
    std::env::current_exe().ok()?.parent().map(Path::to_path_buf)
}

/// Folders searched for files shipped with the app, most specific first.
pub fn resource_dirs() -> Vec<PathBuf> {
    let mut dirs = Vec::new();
    if let Some(exe) = exe_dir() {
        // Inside DynamiteTTS.app the executable is in Contents/MacOS, resources in Contents/Resources.
        #[cfg(target_os = "macos")]
        dirs.push(exe.join("../Resources"));
        // Linux installs put the binary in ~/.local/share/dynamite-tts/app/bin.
        #[cfg(all(unix, not(target_os = "macos")))]
        dirs.push(exe.join(".."));
        dirs.push(exe);
    }
    if let Ok(cwd) = std::env::current_dir() {
        dirs.push(cwd);
    }
    dirs
}

/// The Unix socket's folder: $XDG_RUNTIME_DIR, else a private folder under /tmp.
#[cfg(unix)]
pub fn runtime_dir() -> PathBuf {
    if let Some(dir) = std::env::var_os("XDG_RUNTIME_DIR").map(PathBuf::from) {
        if dir.is_dir() {
            return dir;
        }
    }
    // SAFETY: getuid has no preconditions.
    let uid = unsafe { libc_getuid() };
    PathBuf::from(format!("/tmp/dynamite-tts-{uid}"))
}

#[cfg(unix)]
unsafe extern "C" {
    #[link_name = "getuid"]
    fn libc_getuid() -> u32;
}
