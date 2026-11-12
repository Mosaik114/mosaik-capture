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
    BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS, FILE_ID_INFO, FILE_SHARE_DELETE,
    FILE_SHARE_READ, FILE_SHARE_WRITE, FileIdInfo, GetFileInformationByHandle,
    GetFileInformationByHandleEx,
};
use windows_sys::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

/// Room for long (`\\?\`) process image paths.
const IMAGE_PATH_CAPACITY: u32 = 32_768;

/// The identity of a file. Two paths with the same identity name the same file,
/// whatever their spelling (case, 8.3 names, `\\?\` prefixes, junctions).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct FileIdentity {
    /// 64-bit volume serial and 128-bit file id (`FileIdInfo`; needed on `ReFS`, whose
    /// ids do not fit into 64 bits).
    wide: Option<(u64, [u8; 16])>,
    /// 32-bit volume serial and 64-bit file index (`GetFileInformationByHandle`),
    /// the fallback where `FileIdInfo` is not supported.
    narrow: Option<(u32, u64)>,
}

impl FileIdentity {
    /// Whether both describe the same file, using the most precise id both have.
    fn same_as(self, other: Self) -> Option<bool> {
        match (self.wide, other.wide, self.narrow, other.narrow) {
            (Some(a), Some(b), _, _) => Some(a == b),
            (_, _, Some(a), Some(b)) => Some(a == b),
            _ => None,
        }
    }
}

/// Reads the identity of the file at `path`.
fn file_id(path: &Path) -> Option<FileIdentity> {
    // Access mask 0: only metadata is queried, so even files opened exclusively by
    // others can be identified. FILE_FLAG_BACKUP_SEMANTICS lets the same call open
    // directories, so any kind of path can be compared.
    let file = OpenOptions::new()
        .access_mode(0)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
        .open(path)
        .ok()?;
    let handle = file.as_raw_handle();

    // SAFETY: plain C struct for which all-zero is valid.
    let mut id: FILE_ID_INFO = unsafe { mem::zeroed() };
    let size = u32::try_from(mem::size_of::<FILE_ID_INFO>()).ok()?;
    // SAFETY: the handle is open; `id` is a FILE_ID_INFO of `size` bytes, as
    // FileIdInfo requires, and outlives the call.
    let wide_ok =
        unsafe { GetFileInformationByHandleEx(handle, FileIdInfo, (&raw mut id).cast(), size) }
            != 0;

    // SAFETY: plain C struct for which all-zero is valid.
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { mem::zeroed() };
    // SAFETY: the handle is open for the duration of the call; `info` outlives it.
    let narrow_ok = unsafe { GetFileInformationByHandle(handle, &raw mut info) } != 0;
    let index = (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow);

    let identity = FileIdentity {
        wide: wide_ok.then_some((id.VolumeSerialNumber, id.FileId.Identifier)),
        narrow: narrow_ok.then_some((info.dwVolumeSerialNumber, index)),
    };
    (identity.wide.is_some() || identity.narrow.is_some()).then_some(identity)
}

/// Whether both paths name the same existing file: by file identity, or, if that
/// cannot be read, by canonical path.
pub fn same_file(a: &Path, b: &Path) -> bool {
    let by_identity = match (file_id(a), file_id(b)) {
        (Some(a), Some(b)) => a.same_as(b),
        _ => None,
    };
    by_identity
        .unwrap_or_else(|| matches!((a.canonicalize(), b.canonicalize()), (Ok(a), Ok(b)) if a == b))
}

/// The id of the process that owns `window` (0 if the window is gone).
pub fn window_process_id(window: HWND) -> u32 {
    let mut pid = 0u32;
    // SAFETY: `pid` outlives the call; a stale handle just yields 0.
    unsafe { GetWindowThreadProcessId(window, &raw mut pid) };
    pid
}

/// Whether `window` belongs to a process running `expected` (compared as a file).
///
/// The owner is checked again afterwards: had the window been destroyed and its
/// handle reused by another process meanwhile, the process id would differ.
pub fn window_runs(window: HWND, expected: &Path) -> bool {
    let pid = window_process_id(window);
    let matches = image_of_process(pid).is_some_and(|image| same_file(&image, expected));
    matches && window_process_id(window) == pid
}

/// The executable of process `pid`.
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
    use std::ptr;

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
    fn identities_prefer_the_wide_file_id() {
        let wide = |id: u8| Some((1, [id; 16]));
        let a = FileIdentity { wide: wide(1), narrow: Some((1, 7)) };
        let b = FileIdentity { wide: wide(2), narrow: Some((1, 7)) };
        assert_eq!(a.same_as(b), Some(false), "128-bit ids differ, 64-bit ones collide");
        let c = FileIdentity { wide: None, narrow: Some((1, 7)) };
        assert_eq!(a.same_as(c), Some(true), "falls back to the 64-bit index");
        let none = FileIdentity { wide: None, narrow: None };
        assert_eq!(a.same_as(none), None);
        let own = file_id(&std::env::current_exe().unwrap()).unwrap();
        assert!(own.wide.is_some() || own.narrow.is_some());
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
        assert!(same_file(&own, &std::env::current_exe().unwrap()), "{}", own.display());
        assert_eq!(image_of_process(0), None);
    }
}
