use std::ffi::c_void;

use core_foundation::base::{CFGetTypeID, CFRelease, CFTypeRef, TCFType};
use core_foundation::boolean::CFBoolean;
use core_foundation::dictionary::CFDictionary;
use core_foundation::string::{CFString, CFStringGetTypeID, CFStringRef};
use core_graphics::event::{CGEvent, CGEventFlags, CGEventTapLocation};
use core_graphics::event_source::{CGEventSource, CGEventSourceStateID};

use super::Capture;

type AXUIElementRef = *const c_void;

#[link(name = "ApplicationServices", kind = "framework")]
unsafe extern "C" {
    fn AXUIElementCreateSystemWide() -> AXUIElementRef;
    fn AXUIElementCopyAttributeValue(element: AXUIElementRef, attribute: CFStringRef, value: *mut CFTypeRef) -> i32;
    fn AXIsProcessTrusted() -> u8;
    fn AXIsProcessTrustedWithOptions(options: *const c_void) -> u8;
}

pub fn is_trusted() -> bool {
    // SAFETY: no preconditions.
    unsafe { AXIsProcessTrusted() != 0 }
}

/// Shows the system prompt that sends the user to the privacy list for this permission.
pub fn request_permission() {
    let key = CFString::from_static_string("AXTrustedCheckOptionPrompt");
    let options = CFDictionary::from_CFType_pairs(&[(key.as_CFType(), CFBoolean::true_value().as_CFType())]);
    // SAFETY: a valid CFDictionary for the call's duration.
    unsafe { AXIsProcessTrustedWithOptions(options.as_concrete_TypeRef() as *const c_void) };
}

pub fn missing_permission() -> Option<String> {
    (!is_trusted()).then(|| {
        format!("Allow Dynamite TTS under Privacy & Security → {} so it can read the highlight", permission_pane())
    })
}

/// macOS 27 renamed this list. The setting is the same one.
fn permission_pane() -> &'static str {
    if macos_major() >= 27 { "Device Control and Data Access" } else { "Accessibility" }
}

fn macos_major() -> u32 {
    let Ok(text) = std::fs::read_to_string("/System/Library/CoreServices/SystemVersion.plist") else {
        return 0;
    };
    let Some(start) = text.find("<key>ProductVersion</key>") else { return 0 };
    let rest = &text[start..];
    let Some(open) = rest.find("<string>") else { return 0 };
    let rest = &rest[open + "<string>".len()..];
    let Some(end) = rest.find("</string>") else { return 0 };
    rest[..end].split('.').next().and_then(|n| n.parse().ok()).unwrap_or(0)
}

/// Copies an attribute; the caller releases the result.
fn attribute(element: AXUIElementRef, name: &'static str) -> Option<CFTypeRef> {
    let name = CFString::from_static_string(name);
    let mut value: CFTypeRef = std::ptr::null();
    // SAFETY: element is a live AXUIElement; value receives a +1 reference on success.
    let status = unsafe { AXUIElementCopyAttributeValue(element, name.as_concrete_TypeRef(), &mut value) };
    (status == 0 && !value.is_null()).then_some(value)
}

fn string_attribute(element: AXUIElementRef, name: &'static str) -> Option<String> {
    let value = attribute(element, name)?;
    // SAFETY: value is a +1 CF object; it is either wrapped (and released by the wrapper) or released here.
    unsafe {
        if CFGetTypeID(value) == CFStringGetTypeID() {
            Some(CFString::wrap_under_create_rule(value as CFStringRef).to_string())
        } else {
            CFRelease(value);
            None
        }
    }
}

fn accessibility_selection(include_context: bool) -> Option<(String, Option<String>)> {
    // SAFETY: returns a +1 AXUIElement, released below.
    let system = unsafe { AXUIElementCreateSystemWide() };
    if system.is_null() {
        return None;
    }
    let focused = attribute(system, "AXFocusedUIElement");
    unsafe { CFRelease(system) };
    let focused = focused?;
    let selected = string_attribute(focused, "AXSelectedText");
    let context = if include_context { string_attribute(focused, "AXValue") } else { None };
    unsafe { CFRelease(focused) };
    selected.filter(|s| !s.trim().is_empty()).map(|s| (s, context))
}

fn post_command_c() {
    let Ok(source) = CGEventSource::new(CGEventSourceStateID::HIDSystemState) else { return };
    // Lift Command, Shift, Option and Control (both sides) so the app sees a plain Cmd+C.
    for keycode in [0x37u16, 0x36, 0x38, 0x3C, 0x3A, 0x3D, 0x3B, 0x3E] {
        if let Ok(up) = CGEvent::new_keyboard_event(source.clone(), keycode, false) {
            up.set_flags(CGEventFlags::CGEventFlagNull);
            up.post(CGEventTapLocation::HID);
        }
    }
    for down in [true, false] {
        if let Ok(event) = CGEvent::new_keyboard_event(source.clone(), 0x08, down) {
            event.set_flags(CGEventFlags::CGEventFlagCommand);
            event.post(CGEventTapLocation::HID);
        }
    }
}

pub fn capture(include_context: bool) -> Capture {
    // Synthetic Cmd+C is dropped for an untrusted app, so the clipboard wait would only
    // burn ~800ms and look like an empty selection.
    if !is_trusted() {
        crate::log!("capture skipped: accessibility not granted");
        return Capture::default();
    }
    if let Some((text, context)) = accessibility_selection(include_context) {
        return Capture { text: Some(text), context };
    }
    // Apps without Accessibility text (browsers, Electron): copy. Clearing first makes any new
    // text a sign the copy worked, since arboard does not expose the pasteboard's change count.
    let text = super::clipboard::copy_selection(true, post_command_c, || {
        arboard::Clipboard::new().ok().and_then(|mut cb| cb.get_text().ok()).is_some_and(|t| !t.is_empty())
    });
    Capture { text, context: None }
}
