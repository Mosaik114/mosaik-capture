//! The application icon at the exact sizes a DPI needs, loaded with
//! `LoadIconWithScaleDown` so Windows never has to stretch a smaller image.

use windows_sys::Win32::Foundation::{HINSTANCE, HWND};
use windows_sys::Win32::UI::Controls::LoadIconWithScaleDown;
use windows_sys::Win32::UI::HiDpi::GetSystemMetricsForDpi;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    DestroyIcon, HICON, ICON_BIG, ICON_SMALL, LoadIconW, SM_CXICON, SM_CXSMICON,
    SYSTEM_METRICS_INDEX, SendMessageW, WM_SETICON,
};
use windows_sys::core::PCWSTR;

use super::layout::Dpi;

/// Resource id of the application icon group (see `build.rs`).
const APP_ICON_RESOURCE: u16 = 1;

/// `MAKEINTRESOURCEW(APP_ICON_RESOURCE)`.
fn app_icon_name() -> PCWSTR {
    usize::from(APP_ICON_RESOURCE) as PCWSTR
}

/// The shared application icon at the default size, for the window class.
pub fn class_icon(instance: HINSTANCE) -> HICON {
    // SAFETY: MAKEINTRESOURCE-style ids are valid name arguments for LoadIconW.
    unsafe { LoadIconW(instance, app_icon_name()) }
}

/// An icon owned by us, destroyed on drop. May be null if the resource is missing;
/// Windows then simply shows no (or the default) icon.
#[derive(Debug)]
pub struct OwnedIcon(HICON);

impl OwnedIcon {
    /// Loads the application icon at the size of `metric` (e.g. `SM_CXSMICON`) for
    /// `dpi`, scaling down from the next larger image if there is no exact match.
    pub fn load(instance: HINSTANCE, metric: SYSTEM_METRICS_INDEX, dpi: Dpi) -> Self {
        // SAFETY: no pointer arguments.
        let size = unsafe { GetSystemMetricsForDpi(metric, dpi.value()) };
        let mut icon: HICON = std::ptr::null_mut();
        // SAFETY: loads a resource of our own module by id into `icon`, which outlives
        // the call; the icon is owned by us.
        let result =
            unsafe { LoadIconWithScaleDown(instance, app_icon_name(), size, size, &raw mut icon) };
        if result < 0 {
            icon = std::ptr::null_mut();
        }
        Self(icon)
    }

    /// The raw handle; valid as long as `self` lives.
    pub fn handle(&self) -> HICON {
        self.0
    }
}

impl Drop for OwnedIcon {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: the icon was created for us by LoadIconWithScaleDown.
            unsafe { DestroyIcon(self.0) };
        }
    }
}

/// The small (caption, Alt+Tab) and big (taskbar) icons of a window.
#[derive(Debug)]
pub struct WindowIcons {
    small: OwnedIcon,
    big: OwnedIcon,
}

impl WindowIcons {
    /// Loads both icons for `dpi`.
    pub fn load(instance: HINSTANCE, dpi: Dpi) -> Self {
        Self {
            small: OwnedIcon::load(instance, SM_CXSMICON, dpi),
            big: OwnedIcon::load(instance, SM_CXICON, dpi),
        }
    }

    /// Hands both icons to `window`. The previous icons must stay alive until this
    /// returns, so the caller drops them afterwards.
    pub fn apply(&self, window: HWND) {
        for (kind, icon) in [(ICON_SMALL, &self.small), (ICON_BIG, &self.big)] {
            // SAFETY: WM_SETICON takes an icon handle, not a pointer.
            unsafe { SendMessageW(window, WM_SETICON, kind as usize, icon.handle() as isize) };
        }
    }
}
