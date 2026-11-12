//! Safe access to the settings window's child controls.
//!
//! A [`Control`] can only be obtained by creating a child of our window
//! ([`Control::create`]), by looking one up with `GetDlgItem` on our window
//! ([`Control::child`]) or from the `DRAWITEMSTRUCT` Windows hands us for one of our
//! owner-drawn buttons. User32 validates window handles itself, so even a stale or
//! null handle only makes a call fail; the remaining obligation, valid pointers, is
//! upheld by these methods.

use std::ffi::{OsStr, OsString};
use std::ptr;

use windows_sys::Win32::Foundation::{GetLastError, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::HFONT;
use windows_sys::Win32::UI::Controls::{BST_CHECKED, DRAWITEMSTRUCT, EM_LIMITTEXT};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BM_GETCHECK, BM_SETCHECK, CB_ADDSTRING, CB_GETCURSEL, CB_SETCURSEL, CreateWindowExW,
    GetDlgItem, GetWindowTextLengthW, GetWindowTextW, HMENU, SWP_NOACTIVATE, SWP_NOZORDER,
    SendMessageW, SetWindowPos, SetWindowTextW, WINDOW_EX_STYLE, WINDOW_STYLE, WM_SETFONT,
    WS_CHILD, WS_VISIBLE,
};
use windows_sys::core::PCWSTR;

use crate::wide::{from_wide, to_wide};

/// A child control of the settings window.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Control(HWND);

/// What [`Control::create`] needs to know.
#[derive(Clone, Copy, Debug)]
pub(super) struct Creation<'a> {
    /// The settings window.
    pub parent: HWND,
    /// Our module.
    pub instance: HINSTANCE,
    /// Window class, e.g. `w!("BUTTON")`.
    pub class: PCWSTR,
    /// Initial text.
    pub text: &'a str,
    /// Extended window style.
    pub ex_style: WINDOW_EX_STYLE,
    /// Window style (`WS_CHILD | WS_VISIBLE` are added).
    pub style: WINDOW_STYLE,
    /// `[x, y, width, height]` in pixels.
    pub rect: [i32; 4],
    /// Control id (0 for labels).
    pub id: i32,
}

impl Control {
    /// Creates a child control; on failure, the Windows error code.
    pub(super) fn create(creation: &Creation<'_>) -> Result<Self, u32> {
        let text = to_wide(creation.text);
        let [x, y, width, height] = creation.rect;
        // SAFETY: class and text are NUL-terminated and outlive the call; the control
        // id is passed in the HMENU slot as child windows require.
        let control = unsafe {
            CreateWindowExW(
                creation.ex_style,
                creation.class,
                text.as_ptr(),
                WS_CHILD | WS_VISIBLE | creation.style,
                x,
                y,
                width,
                height,
                creation.parent,
                creation.id as HMENU,
                creation.instance,
                ptr::null(),
            )
        };
        if control.is_null() {
            // SAFETY: reads the calling thread's last-error value, set by CreateWindowExW.
            return Err(unsafe { GetLastError() });
        }
        Ok(Self(control))
    }

    /// The control with `id` on our window `parent`.
    pub(super) fn child(parent: HWND, id: i32) -> Self {
        // SAFETY: no pointer arguments.
        Self(unsafe { GetDlgItem(parent, id) })
    }

    /// The owner-drawn button described by a `WM_DRAWITEM` sent to our window.
    pub(super) fn of_draw_item(item: &DRAWITEMSTRUCT) -> Self {
        Self(item.hwndItem)
    }

    /// The raw handle, e.g. to compare with a message parameter.
    pub(super) fn handle(self) -> HWND {
        self.0
    }

    /// Sends a message to the control.
    ///
    /// # Safety
    ///
    /// If `message` interprets `wparam` or `lparam` as a pointer, it must be valid for
    /// the duration of the call.
    unsafe fn send(self, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        // SAFETY: forwarded to the caller.
        unsafe { SendMessageW(self.0, message, wparam, lparam) }
    }

    /// Moves and resizes the control (`[x, y, width, height]` in pixels).
    pub(super) fn move_to(self, [x, y, width, height]: [i32; 4]) {
        let flags = SWP_NOZORDER | SWP_NOACTIVATE;
        // SAFETY: no pointer arguments.
        unsafe { SetWindowPos(self.0, ptr::null_mut(), x, y, width, height, flags) };
    }

    /// Replaces the control's text (any UTF-16, e.g. a path, round-trips).
    pub(super) fn set_text(self, text: impl AsRef<OsStr>) {
        let text = to_wide(text);
        // SAFETY: `text` is NUL-terminated and outlives the call.
        unsafe { SetWindowTextW(self.0, text.as_ptr()) };
    }

    /// The control's full text, exactly as stored.
    pub(super) fn text_os(self) -> OsString {
        // SAFETY: no pointer arguments.
        let length = unsafe { GetWindowTextLengthW(self.0) };
        let mut buffer = vec![0u16; usize::try_from(length).unwrap_or(0) + 1];
        let capacity = i32::try_from(buffer.len()).unwrap_or(i32::MAX);
        // SAFETY: `buffer` has room for `capacity` characters including the NUL.
        unsafe { GetWindowTextW(self.0, buffer.as_mut_ptr(), capacity) };
        from_wide(&buffer)
    }

    /// The control's text as Rust text (unpaired surrogates become U+FFFD).
    pub(super) fn text(self) -> String {
        self.text_os().to_string_lossy().into_owned()
    }

    /// Sets the control's font and redraws it. The font must stay alive until the
    /// next `set_font`.
    pub(super) fn set_font(self, font: HFONT) {
        // SAFETY: WM_SETFONT takes a font handle, not a pointer.
        unsafe { self.send(WM_SETFONT, font as WPARAM, 1) };
    }

    /// Limits the number of characters an edit control accepts.
    pub(super) fn limit_text(self, max_chars: usize) {
        // SAFETY: EM_LIMITTEXT takes a plain count.
        unsafe { self.send(EM_LIMITTEXT, max_chars, 0) };
    }

    /// Checks or unchecks a check box.
    pub(super) fn set_checked(self, checked: bool) {
        // SAFETY: BM_SETCHECK takes a plain state value.
        unsafe { self.send(BM_SETCHECK, usize::from(checked), 0) };
    }

    /// Whether a check box is checked.
    pub(super) fn is_checked(self) -> bool {
        // SAFETY: BM_GETCHECK takes no parameters.
        let state = unsafe { self.send(BM_GETCHECK, 0, 0) };
        isize::try_from(BST_CHECKED).is_ok_and(|checked| state == checked)
    }

    /// Appends an entry to a drop-down list.
    pub(super) fn add_entry(self, text: &str) {
        let text = to_wide(text);
        // SAFETY: CB_ADDSTRING reads the NUL-terminated `text`, which outlives the call.
        unsafe { self.send(CB_ADDSTRING, 0, text.as_ptr() as LPARAM) };
    }

    /// Selects an entry of a drop-down list.
    pub(super) fn select(self, index: usize) {
        // SAFETY: CB_SETCURSEL takes a plain index.
        unsafe { self.send(CB_SETCURSEL, index, 0) };
    }

    /// The selected entry of a drop-down list, if any.
    pub(super) fn selection(self) -> Option<usize> {
        // SAFETY: CB_GETCURSEL takes no parameters.
        let index = unsafe { self.send(CB_GETCURSEL, 0, 0) };
        // CB_ERR (-1) is the only negative result.
        usize::try_from(index).ok()
    }
}
