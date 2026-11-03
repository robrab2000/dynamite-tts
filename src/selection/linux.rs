use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use super::Capture;

fn on_path(program: &str) -> bool {
    std::env::var_os("PATH").is_some_and(|path| std::env::split_paths(&path).any(|d| d.join(program).is_file()))
}

/// Runs a reader with a 3 second limit; its stdout when it succeeds.
fn run(program: &str, args: &[&str]) -> Option<String> {
    let mut child = Command::new(program)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    let deadline = Instant::now() + Duration::from_secs(3);
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                let output = child.wait_with_output().ok()?;
                return status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned());
            }
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return None;
            }
        }
    }
}

fn wayland() -> bool {
    std::env::var_os("WAYLAND_DISPLAY").is_some()
}

fn read(primary: bool) -> Option<String> {
    if wayland() && on_path("wl-paste") {
        let mut args = vec!["--no-newline", "--type", "text/plain;charset=utf-8"];
        if primary {
            args.insert(0, "--primary");
        }
        if let Some(t) = run("wl-paste", &args) {
            return Some(t);
        }
        let plain: &[&str] = if primary { &["--no-newline", "--primary"] } else { &["--no-newline"] };
        if let Some(t) = run("wl-paste", plain) {
            return Some(t);
        }
    }
    if on_path("xclip") {
        if let Some(t) = run("xclip", &["-o", "-selection", if primary { "primary" } else { "clipboard" }]) {
            return Some(t);
        }
    }
    if on_path("xsel") {
        if let Some(t) = run("xsel", &["-o", if primary { "--primary" } else { "--clipboard" }]) {
            return Some(t);
        }
    }
    None
}

/// The highlight, falling back to the clipboard when nothing is highlighted.
pub fn capture() -> Capture {
    let text = [true, false].into_iter().find_map(|primary| read(primary).filter(|t| !t.trim().is_empty()));
    Capture { text, context: None }
}

pub fn missing_tools() -> Option<String> {
    if wayland() && !on_path("wl-paste") {
        return Some("Install wl-clipboard so the highlight can be read".into());
    }
    if !wayland() && !on_path("xclip") && !on_path("xsel") {
        return Some("Install xclip so the highlight can be read".into());
    }
    None
}
