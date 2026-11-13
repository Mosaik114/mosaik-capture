//! Which executable owns a window. Used to trust only our own windows: the engine's
//! (its executable next to ours) and, for single-instance forwarding, the primary
//! instance's.
//!
//! The threat is modest — any process of the same user could message our windows
//! anyway — so the check is the image path Windows reports for the owning process,
//! compared with the expected path the way the file system compares names (ordinal,
//! case-insensitive). Other spellings of the same file (8.3 names, junctions) do not
//! match; both sides come from Windows in the same normalised form.

use std::ffi::OsString;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};

use windows_sys::Win32::Foundation::{CloseHandle, HWND};
use windows_sys::Win32::Globalization::{CSTR_EQUAL, CompareStringOrdinal};
use windows_sys::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

/// Room for long process image paths.
const IMAGE_PATH_CAPACITY: u32 = 32_768;
/// `\\?\`, which `same_path` ignores.
const VERBATIM_PREFIX: [u16; 4] = [0x5C, 0x5C, 0x3F, 0x5C];

/// The id of the process that owns `window` (0 if the window is gone).
pub fn window_process_id(window: HWND) -> u32 {
    let mut pid = 0u32;
    // SAFETY: `pid` outlives the call; a stale handle just yields 0.
    unsafe { GetWindowThreadProcessId(window, &raw mut pid) };
    pid
}

/// Whether `window` belongs to a process running `expected`.
///
/// The owner is checked again afterwards: had the window been destroyed and its
/// handle reused by another process meanwhile, the process id would differ.
pub fn window_runs(window: HWND, expected: &Path) -> bool {
    let pid = window_process_id(window);
    let matches = image_of_process(pid).is_some_and(|image| same_path(&image, expected));
    matches && window_process_id(window) == pid
}

pub fn image_of_process(pid: u32) -> Option<PathBuf> {
    if pid == 0 {
        return None;
    }
    // SAFETY: no pointer arguments; the handle is closed below.
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if process.is_null() {
        return None;
    }
    let mut buffer = vec![0u16; IMAGE_PATH_CAPACITY as usize];
    let mut length = IMAGE_PATH_CAPACITY;
    // SAFETY: `buffer` holds `length` characters; `length` receives the copied count.
    let ok = unsafe {
        QueryFullProcessImageNameW(
            process,
            PROCESS_NAME_WIN32,
            buffer.as_mut_ptr(),
            &raw mut length,
        )
    } != 0;
    // SAFETY: the handle came from OpenProcess and is closed exactly once.
    unsafe { CloseHandle(process) };
    buffer.truncate(length as usize);
    ok.then(|| PathBuf::from(OsString::from_wide(&buffer)))
}

/// Compares like NTFS compares names (`CompareStringOrdinal`, ignoring case), with
/// or without a `\\?\` prefix.
fn same_path(a: &Path, b: &Path) -> bool {
    let (a, b): (Vec<u16>, Vec<u16>) =
        (a.as_os_str().encode_wide().collect(), b.as_os_str().encode_wide().collect());
    let unprefixed =
        |units: &[u16]| units.strip_prefix(&VERBATIM_PREFIX).unwrap_or(units).to_owned();
    let (a, b) = (unprefixed(&a), unprefixed(&b));
    let (Ok(a_len), Ok(b_len)) = (i32::try_from(a.len()), i32::try_from(b.len())) else {
        return false;
    };
    // SAFETY: both buffers hold exactly the given number of units.
    unsafe { CompareStringOrdinal(a.as_ptr(), a_len, b.as_ptr(), b_len, 1) == CSTR_EQUAL }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr;

    #[test]
    fn paths_are_compared_like_the_file_system() {
        let exe = std::env::current_exe().unwrap();
        let upper = PathBuf::from(exe.to_string_lossy().to_uppercase());
        assert!(same_path(&exe, &upper), "case does not matter");
        assert!(same_path(Path::new(r"C:\Ärger\a.exe"), Path::new(r"c:\äRGER\A.EXE")), "non-ASCII");
        assert!(!same_path(Path::new(r"C:\Grüße"), Path::new(r"C:\GRÜSSE")), "ß is not SS");
        let verbatim = PathBuf::from(format!(r"\\?\{}", exe.display()));
        assert!(same_path(&exe, &verbatim), "verbatim prefix does not matter");
        assert!(!same_path(&exe, &exe.with_file_name("MosaikCapture.Engine.exe")));
        assert!(!same_path(&exe, Path::new("")));
    }

    #[test]
    fn window_owner_checks() {
        let exe = std::env::current_exe().unwrap();
        assert!(!window_runs(ptr::null_mut(), &exe), "no window");
        assert_eq!(window_process_id(ptr::null_mut()), 0);
    }

    #[test]
    fn process_images_are_resolved() {
        let own = image_of_process(std::process::id()).expect("own process");
        assert!(same_path(&own, &std::env::current_exe().unwrap()), "{}", own.display());
        assert_eq!(image_of_process(0), None);
    }
}
