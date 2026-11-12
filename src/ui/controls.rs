//! Safe helpers for reading and writing standard controls.
//!
//! All functions take window handles by value. User32 validates handles itself, so a
//! stale or null handle makes a call fail harmlessly instead of causing undefined
//! behaviour; the remaining obligation, valid pointers, is upheld by these wrappers.

use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::HFONT;
use windows_sys::Win32::UI::Controls::{BST_CHECKED, EM_LIMITTEXT};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BM_GETCHECK, BM_SETCHECK, CB_ADDSTRING, CB_GETCURSEL, CB_SETCURSEL, GetDlgItem,
    GetWindowTextLengthW, GetWindowTextW, SendMessageW, SetWindowTextW, WM_SETFONT,
};

use crate::wide::{from_wide, to_wide};

/// Sends a message to a control.
///
/// # Safety
///
/// If `message` interprets `wparam` or `lparam` as a pointer, it must be valid for
/// the duration of the call.
unsafe fn send(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    // SAFETY: forwarded to the caller.
    unsafe { SendMessageW(window, message, wparam, lparam) }
}

/// The child control with the given id.
pub fn child(parent: HWND, id: i32) -> HWND {
    // SAFETY: no pointer arguments.
    unsafe { GetDlgItem(parent, id) }
}

/// Replaces a window's text.
pub fn set_text(window: HWND, text: &str) {
    let text = to_wide(text);
    // SAFETY: `text` is NUL-terminated and outlives the call.
    unsafe { SetWindowTextW(window, text.as_ptr()) };
}

/// A window's full text.
pub fn text(window: HWND) -> String {
    // SAFETY: no pointer arguments.
    let length = unsafe { GetWindowTextLengthW(window) };
    let mut buffer = vec![0u16; usize::try_from(length).unwrap_or(0) + 1];
    let capacity = i32::try_from(buffer.len()).unwrap_or(i32::MAX);
    // SAFETY: `buffer` has room for `capacity` characters including the NUL.
    unsafe { GetWindowTextW(window, buffer.as_mut_ptr(), capacity) };
    from_wide(&buffer)
}

/// Sets a control's font and redraws it. The font must outlive the control.
pub fn set_font(window: HWND, font: HFONT) {
    // SAFETY: WM_SETFONT takes a font handle, not a pointer.
    unsafe { send(window, WM_SETFONT, font as WPARAM, 1) };
}

/// Limits the number of characters an edit control accepts.
pub fn limit_text(window: HWND, max_chars: usize) {
    // SAFETY: EM_LIMITTEXT takes a plain count.
    unsafe { send(window, EM_LIMITTEXT, max_chars, 0) };
}

/// Checks or unchecks a check box.
pub fn set_checked(window: HWND, checked: bool) {
    // SAFETY: BM_SETCHECK takes a plain state value.
    unsafe { send(window, BM_SETCHECK, usize::from(checked), 0) };
}

/// Whether a check box is checked.
pub fn is_checked(window: HWND) -> bool {
    // SAFETY: BM_GETCHECK takes no parameters.
    let state = unsafe { send(window, BM_GETCHECK, 0, 0) };
    isize::try_from(BST_CHECKED).is_ok_and(|checked| state == checked)
}

/// Appends an entry to a drop-down list.
pub fn add_entry(window: HWND, text: &str) {
    let text = to_wide(text);
    // SAFETY: CB_ADDSTRING reads the NUL-terminated `text`, which outlives the call.
    unsafe { send(window, CB_ADDSTRING, 0, text.as_ptr() as LPARAM) };
}

/// Selects an entry of a drop-down list.
pub fn select(window: HWND, index: usize) {
    // SAFETY: CB_SETCURSEL takes a plain index.
    unsafe { send(window, CB_SETCURSEL, index, 0) };
}

/// The selected entry of a drop-down list, if any.
pub fn selection(window: HWND) -> Option<usize> {
    // SAFETY: CB_GETCURSEL takes no parameters.
    let index = unsafe { send(window, CB_GETCURSEL, 0, 0) };
    // CB_ERR (-1) is the only negative result.
    usize::try_from(index).ok()
}
