use windows_sys::Win32::System::DataExchange::GetClipboardSequenceNumber;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, SendInput, VIRTUAL_KEY, VK_CONTROL,
    VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT,
};

use super::Capture;

const VK_C: VIRTUAL_KEY = 0x43;

fn key(vk: VIRTUAL_KEY, up: bool) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk,
                wScan: 0,
                dwFlags: if up { KEYEVENTF_KEYUP } else { 0 },
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn is_down(vk: VIRTUAL_KEY) -> bool {
    // SAFETY: GetAsyncKeyState has no preconditions.
    (unsafe { GetAsyncKeyState(vk as i32) } as u16 & 0x8000) != 0
}

/// Ctrl+C with Shift, Alt and Win lifted first, so the app sees a plain copy even while the
/// user is still holding the rest of the speak shortcut.
fn send_clean_ctrl_c() {
    let mut inputs = Vec::new();
    for vk in [VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN] {
        if is_down(vk) {
            inputs.push(key(vk, true));
        }
    }
    inputs.extend([key(VK_CONTROL, false), key(VK_C, false), key(VK_C, true), key(VK_CONTROL, true)]);
    // SAFETY: the slice is valid for its length and INPUT's size is what SendInput expects.
    unsafe { SendInput(inputs.len() as u32, inputs.as_ptr(), size_of::<INPUT>() as i32) };
}

pub fn capture() -> Capture {
    // SAFETY: no preconditions.
    let before = unsafe { GetClipboardSequenceNumber() };
    let text = super::clipboard::copy_selection(
        false,
        send_clean_ctrl_c,
        || unsafe { GetClipboardSequenceNumber() } != before,
    );
    Capture { text, context: None }
}
