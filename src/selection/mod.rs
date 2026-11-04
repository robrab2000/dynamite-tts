//! Reading the highlighted text.
//!
//! - Windows: a clean synthetic Ctrl+C (held modifiers lifted first), the clipboard read once
//!   its sequence number changes, and the previous clipboard text or image put back afterwards.
//! - macOS: the focused element's selected text through Accessibility (with its full value as
//!   context for Explain and ELI5), falling back to Cmd+C and the pasteboard.
//! - Linux: the PRIMARY selection through wl-paste or xclip/xsel. No keys are sent and the
//!   clipboard is never touched. For Explain and ELI5 the text around it comes from AT-SPI.

#[cfg(target_os = "linux")]
mod atspi;
#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "macos")]
mod macos;
#[cfg(windows)]
mod windows;

#[cfg(any(windows, target_os = "macos"))]
mod clipboard;

#[derive(Debug, Default)]
pub struct Capture {
    pub text: Option<String>,
    /// Text around the selection (the whole field on macOS, the page section on Linux), for
    /// Explain and ELI5.
    pub context: Option<String>,
}

/// Blocks for up to about a second. Call it off the UI thread.
pub fn capture(include_context: bool) -> Capture {
    #[cfg(target_os = "linux")]
    let capture = linux::capture(include_context);
    #[cfg(target_os = "macos")]
    let capture = macos::capture(include_context);
    #[cfg(windows)]
    let capture = windows::capture();
    let _ = include_context;
    Capture { text: capture.text.map(|t| t.trim().to_string()).filter(|t| !t.is_empty()), context: capture.context }
}

/// Something the user must install or allow for capture to work, if anything.
pub fn missing_requirement() -> Option<String> {
    #[cfg(target_os = "linux")]
    return linux::missing_tools();
    #[cfg(target_os = "macos")]
    return macos::missing_permission();
    #[cfg(windows)]
    return None;
}

/// Gets ready to capture the text around the highlight, or stops. Linux: listens on the
/// accessibility bus and turns on Chromium's and Electron's accessibility. No-op elsewhere.
pub fn watch_context(enabled: bool) {
    #[cfg(target_os = "linux")]
    atspi::watch(enabled);
    let _ = enabled;
}

/// Asks for whatever permission capture needs (macOS: Accessibility). No-op elsewhere.
pub fn request_permission() {
    #[cfg(target_os = "macos")]
    macos::request_permission();
}
