//! The notification-area icon and its context menu.

use std::{mem, ptr};

use windows_sys::Win32::Foundation::{HINSTANCE, HWND, POINT};
use windows_sys::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFY_ICON_MESSAGE, NOTIFYICONDATAW,
    Shell_NotifyIconW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyMenu, GetCursorPos, HICON, HMENU, LoadIconW, MF_STRING,
    PostMessageW, SetForegroundWindow, TPM_RETURNCMD, TPM_RIGHTBUTTON, TrackPopupMenu, WM_NULL,
};

use super::layout::id;
use super::strings;
use crate::ipc;
use crate::wide::{copy_to_buffer, to_wide};

/// Identifier of our single icon.
const ICON_ID: u32 = 1;
/// Resource id of the application icon (see `build.rs`).
const APP_ICON_RESOURCE: u16 = 1;

/// Loads the application icon from the executable's resources.
pub fn app_icon(instance: HINSTANCE) -> HICON {
    // SAFETY: MAKEINTRESOURCE-style ids are valid name arguments for LoadIconW.
    unsafe { LoadIconW(instance, usize::from(APP_ICON_RESOURCE) as *const u16) }
}

fn notify(window: HWND, instance: HINSTANCE, message: NOTIFY_ICON_MESSAGE) {
    // SAFETY: NOTIFYICONDATAW is a plain C struct for which all-zero is a valid value.
    let mut data: NOTIFYICONDATAW = unsafe { mem::zeroed() };
    data.cbSize = u32::try_from(mem::size_of::<NOTIFYICONDATAW>()).unwrap_or(u32::MAX);
    data.hWnd = window;
    data.uID = ICON_ID;
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    data.uCallbackMessage = ipc::TRAY_CALLBACK;
    data.hIcon = app_icon(instance);
    copy_to_buffer(&mut data.szTip, strings::TRAY_TOOLTIP);
    // SAFETY: `data` is fully initialised and outlives the call.
    unsafe { Shell_NotifyIconW(message, &raw const data) };
}

/// Adds the icon (also used to restore it after Explorer restarts).
pub fn add(window: HWND, instance: HINSTANCE) {
    notify(window, instance, NIM_ADD);
}

/// Removes the icon.
pub fn remove(window: HWND, instance: HINSTANCE) {
    notify(window, instance, NIM_DELETE);
}

/// A choice from the tray menu.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MenuCommand {
    /// Take a screenshot.
    Screenshot,
    /// Start or stop a recording.
    Recording,
    /// Open the settings window.
    Settings,
    /// Quit the app.
    Quit,
}

impl MenuCommand {
    const ALL: [(Self, i32, &'static str); 4] = [
        (
            Self::Screenshot,
            id::TAKE_SCREENSHOT,
            strings::MENU_SCREENSHOT,
        ),
        (
            Self::Recording,
            id::TOGGLE_RECORDING,
            strings::MENU_RECORDING,
        ),
        (Self::Settings, id::MENU_SETTINGS, strings::MENU_SETTINGS),
        (Self::Quit, id::MENU_QUIT, strings::MENU_QUIT),
    ];

    fn from_id(command: i32) -> Option<Self> {
        Self::ALL
            .iter()
            .find(|(_, id, _)| *id == command)
            .map(|(cmd, _, _)| *cmd)
    }
}

/// A popup menu, destroyed on drop.
struct PopupMenu(HMENU);

impl Drop for PopupMenu {
    fn drop(&mut self) {
        // SAFETY: the menu was created by CreatePopupMenu and is destroyed once.
        unsafe { DestroyMenu(self.0) };
    }
}

/// Shows the context menu at the cursor and returns the chosen command.
pub fn show_menu(window: HWND) -> Option<MenuCommand> {
    // SAFETY: no arguments.
    let menu = PopupMenu(unsafe { CreatePopupMenu() });
    for (_, command, text) in MenuCommand::ALL {
        let text = to_wide(text);
        let command = usize::try_from(command).unwrap_or_default();
        // SAFETY: `text` is NUL-terminated and copied by the menu.
        unsafe { AppendMenuW(menu.0, MF_STRING, command, text.as_ptr()) };
    }
    let mut cursor = POINT { x: 0, y: 0 };
    // SAFETY: `cursor` outlives the calls; the menu and window handles are valid.
    let chosen = unsafe {
        GetCursorPos(&raw mut cursor);
        // Required so the menu closes when the user clicks elsewhere.
        SetForegroundWindow(window);
        TrackPopupMenu(
            menu.0,
            TPM_RETURNCMD | TPM_RIGHTBUTTON,
            cursor.x,
            cursor.y,
            0,
            window,
            ptr::null(),
        )
    };
    drop(menu);
    // SAFETY: posting a message has no memory-safety preconditions. WM_NULL makes the
    // menu behave correctly on the next open (see the TrackPopupMenu docs).
    unsafe { PostMessageW(window, WM_NULL, 0, 0) };
    MenuCommand::from_id(chosen)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn menu_ids_round_trip() {
        for (command, id, _) in MenuCommand::ALL {
            assert_eq!(MenuCommand::from_id(id), Some(command));
        }
        assert_eq!(MenuCommand::from_id(0), None, "dismissed menu");
    }

    #[test]
    fn tooltip_fits_without_truncation() {
        assert!(strings::TRAY_TOOLTIP.encode_utf16().count() < 128);
    }
}
