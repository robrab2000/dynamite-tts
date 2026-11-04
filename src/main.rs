//! Dynamite TTS: highlight text anywhere, press a shortcut, hear it read aloud — on-device.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod app;
mod applog;
mod audio;
mod autostart;
mod download;
mod eq;
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
mod gnome;
mod hotkey;
mod lemonade;
mod llm;
mod paths;
mod phonon;
mod selection;
mod settings;
mod settings_ui;
mod speech;
mod status;
mod text;
mod tray;
mod voices;

use control::COMMANDS;

mod control;

const USAGE: &str = "\
Dynamite TTS — highlight-to-speech

  dynamite-tts                 start the tray app (or open Settings if it is already running)
  dynamite-tts speak           read the current highlight (what the shortcut runs)
  dynamite-tts toggle-mode     Verbatim -> Summary -> Explain -> Verbatim
  dynamite-tts stop | settings | quit | ping
  dynamite-tts --speak TEXT    speak TEXT (starts the app if needed)
  dynamite-tts --version";

/// A GUI-subsystem executable has no console; borrow the terminal it was started from, if any.
#[cfg(windows)]
fn attach_console() {
    use windows_sys::Win32::System::Console::{ATTACH_PARENT_PROCESS, AttachConsole};
    // SAFETY: no preconditions; fails harmlessly without a parent console.
    unsafe { AttachConsole(ATTACH_PARENT_PROCESS) };
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    #[cfg(windows)]
    if !args.is_empty() && args[0] != "--settings-window" {
        attach_console();
    }
    std::process::exit(run(args));
}

fn run(args: Vec<String>) -> i32 {
    match args.first().map(String::as_str) {
        Some("--version" | "-v") => {
            println!("dynamite-tts {}", env!("CARGO_PKG_VERSION"));
            return 0;
        }
        Some("--help" | "-h") => {
            println!("{USAGE}");
            return 0;
        }
        Some("--settings-window") => {
            applog::open("settings");
            return settings_ui::run();
        }
        Some("uninstall-shortcuts") => {
            // Used by the Linux uninstaller: removes the GNOME shortcuts and the login item.
            #[cfg(target_os = "linux")]
            gnome::unbind();
            let _ = autostart::apply(false);
            return 0;
        }
        Some("--speak") => {
            let text = args[1..].join(" ").trim().to_string();
            if text.is_empty() {
                eprintln!("usage: dynamite-tts --speak TEXT");
                return 2;
            }
            if control::send(&format!("speak-text {text}")).is_some() {
                return 0;
            }
            return start(app::Initial::SpeakText(text));
        }
        Some(command) => {
            if !COMMANDS.contains(&command) {
                eprintln!("unknown command '{command}'; one of {}\n\n{USAGE}", COMMANDS.join(", "));
                return 2;
            }
            return match control::send(command) {
                Some(reply) => {
                    if command == "ping" {
                        println!("{reply}");
                    }
                    if reply.starts_with("ok") { 0 } else { 1 }
                }
                // A shortcut pressed before the app started: start it and act once it is up.
                None if matches!(command, "speak" | "settings") => start(app::Initial::Command(command.into())),
                None => {
                    eprintln!("Dynamite TTS is not running.");
                    1
                }
            };
        }
        None => {}
    }
    start(app::Initial::Nothing)
}

fn start(initial: app::Initial) -> i32 {
    let Some(lock) = control::InstanceLock::claim() else {
        // Already running: show its Settings instead of starting a second copy.
        let _ = control::send("settings");
        return 0;
    };
    applog::open("app");
    app::run(initial, lock)
}
