//! Copy-to-read on Windows and macOS: snapshot the clipboard, send the copy shortcut, wait for
//! new text, then put the old contents back a moment later (once the target app has finished
//! with the clipboard).

use std::time::{Duration, Instant};

use arboard::Clipboard;

enum Snapshot {
    Text(String),
    Image(arboard::ImageData<'static>),
    Nothing,
}

fn snapshot(cb: &mut Clipboard) -> Snapshot {
    if let Ok(text) = cb.get_text() {
        return Snapshot::Text(text);
    }
    if let Ok(image) = cb.get_image() {
        return Snapshot::Image(image.to_owned_img());
    }
    Snapshot::Nothing
}

/// `clear_first` empties the clipboard before copying (so any text afterwards is new);
/// `send_copy` sends the shortcut; `changed` tells whether the clipboard was written since.
pub fn copy_selection(clear_first: bool, send_copy: impl FnOnce(), changed: impl Fn() -> bool) -> Option<String> {
    let mut cb = Clipboard::new().ok()?;
    let previous = snapshot(&mut cb);
    if clear_first {
        let _ = cb.clear();
    }
    send_copy();
    let deadline = Instant::now() + Duration::from_millis(800);
    let mut text = None;
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
        if changed() {
            if let Ok(t) = cb.get_text() {
                if !t.trim().is_empty() {
                    text = Some(t);
                    break;
                }
            }
        }
    }
    // Restore even when nothing was copied: the clipboard may have been cleared above.
    if text.is_some() || clear_first {
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(350));
            let Ok(mut cb) = Clipboard::new() else { return };
            let _ = match previous {
                Snapshot::Text(t) => cb.set_text(t),
                Snapshot::Image(i) => cb.set_image(i),
                Snapshot::Nothing => cb.clear(),
            };
        });
    }
    text
}
