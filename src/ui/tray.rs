//! The notification-area icon and its context menu.
//!
//! The icon uses the version-4 callback protocol (`NOTIFYICON_VERSION_4`): Windows
//! reports *what the user meant* — select (click, Space/Enter) or context menu (right
//! click, Shift+F10, menu key, pen) — together with an anchor point, so keyboard and
//! pen users get the same behaviour as mouse users.

use std::{mem, ptr};

use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows_sys::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_SETVERSION, NIN_SELECT,
    NOTIFY_ICON_MESSAGE, NOTIFYICON_VERSION_4, NOTIFYICONDATAW, Shell_NotifyIconW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyMenu, GetSystemMetrics, HICON, HMENU, MF_STRING,
    PostMessageW, SM_MENUDROPALIGNMENT, SetForegroundWindow, TPM_LEFTALIGN, TPM_RETURNCMD,
    TPM_RIGHTALIGN, TPM_RIGHTBUTTON, TRACK_POPUP_MENU_FLAGS, TrackPopupMenu, WM_CONTEXTMENU,
    WM_NULL,
};

use super::layout::id;
use super::strings;
use crate::ipc;
use crate::wide::{copy_to_buffer, to_wide};

/// Identifier of our single icon.
const ICON_ID: u32 = 1;
/// `NIN_KEYSELECT` (`NIN_SELECT | NINF_KEY`): Space or Enter on the focused icon.
const NIN_KEYSELECT: u32 = NIN_SELECT | 1;

fn notify(window: HWND, icon: HICON, message: NOTIFY_ICON_MESSAGE) -> bool {
    // SAFETY: NOTIFYICONDATAW is a plain C struct for which all-zero is a valid value.
    let mut data: NOTIFYICONDATAW = unsafe { mem::zeroed() };
    data.cbSize = u32::try_from(mem::size_of::<NOTIFYICONDATAW>()).unwrap_or(u32::MAX);
    data.hWnd = window;
    data.uID = ICON_ID;
    // Version 4 shows the standard tooltip only with NIF_SHOWTIP.
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    data.uCallbackMessage = ipc::TRAY_CALLBACK;
    data.hIcon = icon;
    data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
    copy_to_buffer(&mut data.szTip, strings::TRAY_TOOLTIP);
    // SAFETY: `data` is fully initialised and outlives the call.
    unsafe { Shell_NotifyIconW(message, &raw const data) != 0 }
}

/// How far the icon has been set up.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrayState {
    /// Not in the notification area (yet, or again after Explorer restarted).
    Missing,
    /// Added, but still on the old callback protocol.
    Added,
    /// Added and switched to the version-4 protocol.
    Ready,
}

impl TrayState {
    /// The state after attempting the step that belongs to `self`.
    const fn after(self, succeeded: bool) -> Self {
        match (self, succeeded) {
            (Self::Missing, true) => Self::Added,
            (Self::Added, true) | (Self::Ready, _) => Self::Ready,
            (state, false) => state,
        }
    }
}

/// Takes the icon from `state` as far as possible towards [`TrayState::Ready`]: adds
/// it if missing, then switches it to the version-4 protocol. A step that fails (the
/// taskbar is not ready, e.g. during logon) is retried by the caller later; a step
/// that succeeded is never repeated, so the icon is not added twice.
pub fn advance(window: HWND, icon: HICON, state: TrayState) -> TrayState {
    let state = match state {
        TrayState::Missing => state.after(notify(window, icon, NIM_ADD)),
        other => other,
    };
    match state {
        TrayState::Added => state.after(notify(window, icon, NIM_SETVERSION)),
        other => other,
    }
}

pub fn remove(window: HWND) {
    notify(window, ptr::null_mut(), NIM_DELETE);
}

/// What the user did with the tray icon.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrayEvent {
    /// Selected the icon (click, Space or Enter): open the settings.
    Select,
    /// Asked for the context menu, anchored at this screen position.
    ContextMenu { x: i32, y: i32 },
}

impl TrayEvent {
    /// Decodes a version-4 callback: the event is `LOWORD(lParam)`, the anchor point
    /// is packed into `wParam` like `GET_X_LPARAM`/`GET_Y_LPARAM`. Raw mouse messages
    /// are ignored; they would duplicate the select/menu events.
    pub fn decode(wparam: WPARAM, lparam: LPARAM) -> Option<Self> {
        let event = u32::try_from(lparam & 0xFFFF).ok()?;
        match event {
            NIN_SELECT | NIN_KEYSELECT => Some(Self::Select),
            WM_CONTEXTMENU => {
                Some(Self::ContextMenu { x: signed_word(wparam), y: signed_word(wparam >> 16) })
            }
            _ => None,
        }
    }
}

/// The low 16 bits of `value` as a signed coordinate (multi-monitor setups can have
/// negative coordinates).
fn signed_word(value: usize) -> i32 {
    i32::from(u16::try_from(value & 0xFFFF).unwrap_or_default().cast_signed())
}

/// Horizontal menu alignment as configured by the user (Tablet PC settings,
/// `SM_MENUDROPALIGNMENT`): right-handed users get menus to the left of the anchor.
const fn menu_alignment(right_aligned: bool) -> TRACK_POPUP_MENU_FLAGS {
    if right_aligned { TPM_RIGHTALIGN } else { TPM_LEFTALIGN }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MenuCommand {
    Screenshot,
    Recording,
    Settings,
    Quit,
}

impl MenuCommand {
    const ALL: [(Self, i32, &'static str); 4] = [
        (Self::Screenshot, id::TAKE_SCREENSHOT, strings::MENU_SCREENSHOT),
        (Self::Recording, id::TOGGLE_RECORDING, strings::MENU_RECORDING),
        (Self::Settings, id::MENU_SETTINGS, strings::MENU_SETTINGS),
        (Self::Quit, id::MENU_QUIT, strings::MENU_QUIT),
    ];

    fn from_id(command: i32) -> Option<Self> {
        Self::ALL.iter().find(|(_, id, _)| *id == command).map(|(cmd, _, _)| *cmd)
    }
}

/// A popup menu, destroyed on drop.
struct PopupMenu(HMENU);

impl PopupMenu {
    fn new() -> Option<Self> {
        // SAFETY: no arguments.
        let menu = unsafe { CreatePopupMenu() };
        (!menu.is_null()).then_some(Self(menu))
    }
}

impl Drop for PopupMenu {
    fn drop(&mut self) {
        // SAFETY: the menu was created by CreatePopupMenu (never null here) and is
        // destroyed once.
        unsafe { DestroyMenu(self.0) };
    }
}

/// Shows the context menu at `(x, y)` and returns the chosen command. Runs a modal
/// menu loop until the user chooses or dismisses.
pub fn show_menu(window: HWND, x: i32, y: i32) -> Option<MenuCommand> {
    let menu = PopupMenu::new()?;
    for (_, command, text) in MenuCommand::ALL {
        let text = to_wide(text);
        let command = usize::try_from(command).unwrap_or_default();
        // SAFETY: `text` is NUL-terminated and copied by the menu.
        unsafe { AppendMenuW(menu.0, MF_STRING, command, text.as_ptr()) };
    }
    // SAFETY: the menu and window handles are valid; no other pointers.
    let chosen = unsafe {
        // Required so the menu closes when the user clicks elsewhere.
        SetForegroundWindow(window);
        let align = menu_alignment(GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0);
        TrackPopupMenu(
            menu.0,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | align,
            x,
            y,
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
    use windows_sys::Win32::UI::WindowsAndMessaging::{WM_LBUTTONUP, WM_RBUTTONUP};

    fn lparam(event: u32) -> LPARAM {
        // HIWORD carries the icon id in version 4.
        isize::try_from((ICON_ID << 16) | event).unwrap()
    }

    #[test]
    fn menus_follow_the_drop_alignment_setting() {
        assert_eq!(menu_alignment(true), TPM_RIGHTALIGN);
        assert_eq!(menu_alignment(false), TPM_LEFTALIGN);
    }

    #[test]
    fn setup_steps_are_not_repeated() {
        assert_eq!(TrayState::Missing.after(false), TrayState::Missing);
        assert_eq!(TrayState::Missing.after(true), TrayState::Added);
        assert_eq!(TrayState::Added.after(false), TrayState::Added, "only SETVERSION is retried");
        assert_eq!(TrayState::Added.after(true), TrayState::Ready);
        assert_eq!(TrayState::Ready.after(false), TrayState::Ready);
    }

    #[test]
    fn version_4_events_are_decoded() {
        assert_eq!(TrayEvent::decode(0, lparam(NIN_SELECT)), Some(TrayEvent::Select));
        assert_eq!(TrayEvent::decode(0, lparam(NIN_KEYSELECT)), Some(TrayEvent::Select));
        let anchor = 0x0064_0032; // y = 100, x = 50
        assert_eq!(
            TrayEvent::decode(anchor, lparam(WM_CONTEXTMENU)),
            Some(TrayEvent::ContextMenu { x: 50, y: 100 })
        );
        assert_eq!(TrayEvent::decode(0, lparam(WM_LBUTTONUP)), None, "raw mouse ignored");
        assert_eq!(TrayEvent::decode(0, lparam(WM_RBUTTONUP)), None);
    }

    #[test]
    fn anchor_coordinates_can_be_negative() {
        let anchor = 0x0010_FF38; // y = 16, x = -200 (monitor left of the primary)
        assert_eq!(
            TrayEvent::decode(anchor, lparam(WM_CONTEXTMENU)),
            Some(TrayEvent::ContextMenu { x: -200, y: 16 })
        );
    }

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
