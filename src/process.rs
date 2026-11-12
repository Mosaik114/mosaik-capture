//! Identifying processes and files: which executable owns a window, and whether two
//! paths name the same file on disk.
//!
//! Used to trust only our own windows: the engine's (by its executable next to ours)
//! and, for single-instance forwarding, the primary instance's.

use std::ffi::OsString;
use std::fs::OpenOptions;
use std::mem;
use std::os::windows::ffi::OsStringExt;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::path::{Path, PathBuf};

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HWND};
use windows_sys::Win32::Storage::FileSystem::{
    BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, GetFileInformationByHandle,
};
use windows_sys::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

/// Room for long (`\\?\`) process image paths.
const IMAGE_PATH_CAPACITY: u32 = 32_768;

/// The identity of a file: volume serial number and file index. Two paths with the
/// same identity name the same file, whatever their spelling (case, 8.3 names,
/// `\\?\` prefixes, junctions).
fn file_id(path: &Path) -> Option<(u32, u64)> {
    // Access mask 0: only metadata is queried, so even files opened exclusively by
    // others can be identified. FILE_FLAG_BACKUP_SEMANTICS lets the same call open
    // directories, so any kind of path can be compared.
    let file = OpenOptions::new()
        .access_mode(0)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
        .open(path)
        .ok()?;
    // SAFETY: plain C struct for which all-zero is valid.
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { mem::zeroed() };
    // SAFETY: the handle is open for the duration of the call; `info` outlives it.
    let ok = unsafe { GetFileInformationByHandle(file.as_raw_handle(), &raw mut info) } != 0;
    let index = (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow);
    ok.then_some((info.dwVolumeSerialNumber, index))
}

/// Whether both paths name the same existing file: by file identity, or, if that
/// cannot be read, by canonical path.
pub fn same_file(a: &Path, b: &Path) -> bool {
    match (file_id(a), file_id(b)) {
        (Some(a), Some(b)) => a == b,
        _ => matches!((a.canonicalize(), b.canonicalize()), (Ok(a), Ok(b)) if a == b),
    }
}

/// The executable of the process that owns `window`.
pub fn process_image(window: HWND) -> Option<PathBuf> {
    let mut pid = 0u32;
    // SAFETY: `pid` outlives the call; a stale handle just yields 0.
    unsafe { GetWindowThreadProcessId(window, &raw mut pid) };
    image_of_process(pid)
}

pub fn image_of_process(pid: u32) -> Option<PathBuf> {
    if pid == 0 {
        return None;
    }
    // SAFETY: no pointer arguments; the handle is closed below.
    let process: HANDLE = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if process.is_null() {
        return None;
    }
    let mut buffer = vec![0u16; usize::try_from(IMAGE_PATH_CAPACITY).unwrap_or(260)];
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
    let length = usize::try_from(length).ok()?;
    ok.then(|| PathBuf::from(OsString::from_wide(&buffer[..length])))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn engine_image_is_compared_by_file_identity() {
        let exe = std::env::current_exe().unwrap();
        let upper = PathBuf::from(exe.to_string_lossy().to_uppercase());
        assert!(same_file(&exe, &upper), "case does not matter");
        let dotted = exe.parent().unwrap().join(".").join(exe.file_name().unwrap());
        assert!(same_file(&exe, &dotted), "spelling does not matter");
        let verbatim = PathBuf::from(format!(r"\\?\{}", exe.display()));
        assert!(same_file(&exe, &verbatim), "verbatim prefix does not matter");

        let other = std::env::temp_dir();
        assert!(!same_file(&exe, &other), "a different file");
        let missing = exe.with_file_name("MosaikCapture.Engine.missing.exe");
        assert!(!same_file(&missing, &missing), "missing files never match");
    }

    #[test]
    fn process_images_are_resolved() {
        let own = image_of_process(std::process::id()).expect("own process");
        assert!(same_file(&own, &std::env::current_exe().unwrap()), "{}", own.display());
        assert_eq!(image_of_process(0), None);
    }
}
