//! The single-instance lock and the local control socket that `dynamite-tts speak` (the GNOME
//! shortcut), the settings window, and the other CLI commands use to reach the running app.
//!
//! A Unix socket in $XDG_RUNTIME_DIR (mode 0600) on Linux and macOS, a per-user named pipe on
//! Windows. One line in (the command), one line out (the reply).

use std::fs::File;
use std::io::{BufRead, BufReader, Read, Write};
use std::sync::Arc;
use std::sync::mpsc::{self, Sender};
use std::time::Duration;

use interprocess::local_socket::{ListenerOptions, Name, Stream, prelude::*};

use crate::{log, paths};

/// Commands the CLI accepts. `speak-text <text>`, `reload`, `suspend-hotkeys` and
/// `resume-hotkeys` are also understood; the settings window sends those.
pub const COMMANDS: &[&str] = &["speak", "toggle-mode", "stop", "settings", "quit", "ping"];

const MAX_COMMAND_BYTES: u64 = 256 * 1024;

#[cfg(unix)]
fn socket_path() -> std::path::PathBuf {
    paths::runtime_dir().join("dynamite-tts.sock")
}

fn name() -> std::io::Result<Name<'static>> {
    #[cfg(unix)]
    {
        use interprocess::local_socket::{GenericFilePath, ToFsName};
        socket_path().to_fs_name::<GenericFilePath>().map(|n| n.into_owned())
    }
    #[cfg(windows)]
    {
        use interprocess::local_socket::{GenericNamespaced, ToNsName};
        let user: String = std::env::var("USERNAME")
            .unwrap_or_default()
            .chars()
            .filter(|c| c.is_ascii_alphanumeric() || *c == '-' || *c == '_')
            .collect();
        format!("dynamite-tts-{user}").to_ns_name::<GenericNamespaced>().map(|n| n.into_owned())
    }
}

/// Holds the instance lock for the life of the process.
pub struct InstanceLock(#[allow(dead_code)] File);

impl InstanceLock {
    /// None when another instance already holds it.
    pub fn claim() -> Option<InstanceLock> {
        let dir = paths::data_dir();
        std::fs::create_dir_all(&dir).ok()?;
        let file = std::fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(dir.join("instance.lock"))
            .ok()?;
        file.try_lock().ok()?;
        Some(InstanceLock(file))
    }
}

/// Sends one command to the running app. Its reply, or None when nothing is listening.
pub fn send(command: &str) -> Option<String> {
    let mut stream = Stream::connect(name().ok()?).ok()?;
    let line = format!("{}\n", command.replace(['\r', '\n'], " "));
    stream.write_all(line.as_bytes()).ok()?;
    stream.flush().ok()?;
    let mut reply = String::new();
    BufReader::new(stream).read_line(&mut reply).ok()?;
    let reply = reply.trim();
    Some(if reply.is_empty() { "ok".into() } else { reply.to_string() })
}

/// Handler: gets each command and a channel for its reply. Runs on the listener's threads, so
/// it should hand the command to the UI thread rather than act on it directly.
pub type Handler = Arc<dyn Fn(String, Sender<String>) + Send + Sync>;

pub fn serve(handler: Handler) -> std::io::Result<()> {
    #[cfg(unix)]
    {
        let path = socket_path();
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
            // Tighten the private /tmp fallback folder; $XDG_RUNTIME_DIR is already the user's own.
            if dir.starts_with("/tmp") {
                use std::os::unix::fs::PermissionsExt;
                let _ = std::fs::set_permissions(dir, std::fs::Permissions::from_mode(0o700));
            }
        }
        // We hold the instance lock, so a socket file left here is stale.
        let _ = std::fs::remove_file(&path);
    }
    let listener = ListenerOptions::new().name(name()?).create_sync()?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(socket_path(), std::fs::Permissions::from_mode(0o600));
    }
    log!("control socket ready");
    std::thread::Builder::new().name("control".into()).spawn(move || {
        for connection in listener.incoming() {
            let Ok(connection) = connection else { continue };
            let handler = handler.clone();
            let _ =
                std::thread::Builder::new().name("control-client".into()).spawn(move || serve_one(connection, handler));
        }
    })?;
    Ok(())
}

fn serve_one(connection: Stream, handler: Handler) {
    let mut reader = BufReader::new(connection);
    let mut line = String::new();
    if (&mut reader).take(MAX_COMMAND_BYTES).read_line(&mut line).is_err() {
        return;
    }
    let command = line.trim().to_string();
    if command.is_empty() {
        return;
    }
    let (tx, rx) = mpsc::channel();
    handler(command, tx);
    let reply = rx.recv_timeout(Duration::from_secs(10)).unwrap_or_else(|_| "timeout".into());
    let mut connection = reader.into_inner();
    let _ = connection.write_all(format!("{reply}\n").as_bytes());
    let _ = connection.flush();
}

/// Removes the socket file at exit (named pipes vanish on their own).
pub fn cleanup() {
    #[cfg(unix)]
    let _ = std::fs::remove_file(socket_path());
}
