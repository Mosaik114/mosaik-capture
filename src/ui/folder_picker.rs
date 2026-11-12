//! The shell's "browse for folder" dialog.

use std::ptr;

use windows_sys::Win32::Foundation::{HWND, LPARAM};
use windows_sys::Win32::System::Com::CoTaskMemFree;
use windows_sys::Win32::UI::Shell::{
    BFFM_INITIALIZED, BFFM_SETSELECTIONW, BIF_NEWDIALOGSTYLE, BIF_RETURNONLYFSDIRS, BROWSEINFOW,
    SHBrowseForFolderW, SHGetPathFromIDListW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::SendMessageW;

use super::strings;
use crate::wide::{from_wide, to_wide};

/// `MAX_PATH`, the buffer size `SHGetPathFromIDListW` writes into.
const MAX_PATH: usize = 260;

/// Lets the user pick a folder, starting at `initial`. `None` if cancelled.
pub fn pick_folder(owner: HWND, initial: &str) -> Option<String> {
    let initial = to_wide(initial);
    let title = to_wide(strings::PICK_FOLDER_TITLE);
    let mut display_name = [0u16; MAX_PATH];
    let info = BROWSEINFOW {
        hwndOwner: owner,
        pidlRoot: ptr::null_mut(),
        pszDisplayName: display_name.as_mut_ptr(),
        lpszTitle: title.as_ptr(),
        ulFlags: BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
        lpfn: Some(preselect),
        lParam: initial.as_ptr() as LPARAM,
        iImage: 0,
    };
    // SAFETY: every pointer in `info` (title, display buffer, initial path) stays
    // alive for the whole modal dialog; COM is initialised on this thread.
    let item = unsafe { SHBrowseForFolderW(&raw const info) };
    if item.is_null() {
        return None;
    }
    let mut path = [0u16; MAX_PATH];
    // SAFETY: `item` is a valid ID list and `path` holds MAX_PATH characters.
    let ok = unsafe { SHGetPathFromIDListW(item, path.as_mut_ptr()) } != 0;
    // SAFETY: the ID list was allocated by the shell with the COM task allocator.
    unsafe { CoTaskMemFree(item.cast()) };
    ok.then(|| from_wide(&path))
}

/// Dialog callback: preselects the folder passed in `lParam` once the dialog is ready.
unsafe extern "system" fn preselect(
    dialog: HWND,
    message: u32,
    _param: LPARAM,
    initial: LPARAM,
) -> i32 {
    if message == BFFM_INITIALIZED {
        // SAFETY: `initial` is the NUL-terminated path from `pick_folder`, which is
        // still running the modal dialog; wParam = TRUE marks it as a path string.
        unsafe { SendMessageW(dialog, BFFM_SETSELECTIONW, 1, initial) };
    }
    0
}
