//! Talking to the capture engine (`MosaikCapture.Engine.exe`), which does the
//! actual screenshots and recordings.
//!
//! A running engine owns a hidden window titled [`WINDOW_TITLE`] and accepts
//! `WM_APP`-based commands. Window titles are not unique, so a window only counts as
//! the engine if its process runs `MosaikCapture.Engine.exe` from this app's folder —
//! compared as the same file on disk, not as path text.
//! If no engine is running, it is started with the capture mode on its command line.
//!
//! All calls run on the UI thread; the only wait is the state query in
//! [`Engine::is_busy`], capped at [`QUERY_TIMEOUT_MS`].

use std::cell::RefCell;
use std::ffi::OsString;
use std::fs::OpenOptions;
use std::mem;
use std::os::windows::ffi::OsStringExt;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::ptr;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HWND};
use windows_sys::Win32::Storage::FileSystem::{
    BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, GetFileInformationByHandle,
};
use windows_sys::Win32::System::Threading::{
    CREATE_NO_WINDOW, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    QueryFullProcessImageNameW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    FindWindowExW, GetWindowThreadProcessId, PostMessageW, SMTO_ABORTIFHUNG, SendMessageTimeoutW,
    WM_APP,
};

use crate::store::PREVIEW_CONFIG_VAR;
use crate::wide::to_wide;

/// Title of the engine's message window.
const WINDOW_TITLE: &str = "MosaikCapture.Engine";
/// File name of the engine, expected next to this executable.
pub const EXE_NAME: &str = "MosaikCapture.Engine.exe";

/// Engine command: take a screenshot.
const MSG_SCREENSHOT: u32 = WM_APP + 1;
/// Engine command: start or stop a recording.
const MSG_RECORDING: u32 = WM_APP + 2;
/// Engine command: shut down (ignored while busy).
const MSG_QUIT: u32 = WM_APP + 3;
/// Engine query: returns state bits, see [`STATE_BUSY`].
const MSG_QUERY_STATE: u32 = WM_APP + 10;
/// State bit: a recording or region selection is in progress.
const STATE_BUSY: usize = 1;
/// Longest the UI thread waits for the state query.
const QUERY_TIMEOUT_MS: u32 = 400;
/// Room for long (`\\?\`) process image paths.
const IMAGE_PATH_CAPACITY: u32 = 32_768;

/// What to capture.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureMode {
    /// A single screenshot.
    Screenshot,
    /// Start or stop a screen recording.
    Recording,
}

impl CaptureMode {
    /// The engine's command-line flag for this mode.
    pub const fn flag(self) -> &'static str {
        match self {
            Self::Screenshot => "--screenshot",
            Self::Recording => "--recording",
        }
    }

    const fn message(self) -> u32 {
        match self {
            Self::Screenshot => MSG_SCREENSHOT,
            Self::Recording => MSG_RECORDING,
        }
    }
}

/// What happened to a capture request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Delivery {
    /// Posted to the running engine.
    Posted,
    /// A new engine was started with the request on its command line.
    Started,
    /// An engine started by us is still coming up; it already carries a request, so
    /// this one is dropped instead of starting a second engine.
    AlreadyStarting,
    /// The engine could neither be reached nor started.
    Failed,
}

impl Delivery {
    /// Whether the user needs to be told that the capture did not happen.
    pub const fn failed(self) -> bool {
        matches!(self, Self::Failed)
    }
}

/// The next step for a capture request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Step {
    Post,
    Wait,
    Spawn,
}

const fn next_step(engine_window: bool, launch_in_progress: bool) -> Step {
    match (engine_window, launch_in_progress) {
        (true, _) => Step::Post,
        (false, true) => Step::Wait,
        (false, false) => Step::Spawn,
    }
}

/// Interprets the engine's reply to [`MSG_QUERY_STATE`]; `None` means no answer.
const fn busy_from_reply(reply: Option<usize>) -> bool {
    match reply {
        Some(state) => state & STATE_BUSY != 0,
        None => true,
    }
}

/// The identity of a file: volume serial number and file index. Two paths with the
/// same identity name the same file, whatever their spelling (case, 8.3 names,
/// `\\?\` prefixes, junctions).
fn file_id(path: &Path) -> Option<(u32, u64)> {
    // Query access only; backup semantics would also allow opening directories.
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
fn same_file(a: &Path, b: &Path) -> bool {
    match (file_id(a), file_id(b)) {
        (Some(a), Some(b)) => a == b,
        _ => matches!((a.canonicalize(), b.canonicalize()), (Ok(a), Ok(b)) if a == b),
    }
}

/// The executable of the process that owns `window`.
fn process_image(window: HWND) -> Option<PathBuf> {
    let mut pid = 0u32;
    // SAFETY: `pid` outlives the call; a stale handle just yields 0.
    unsafe { GetWindowThreadProcessId(window, &raw mut pid) };
    image_of_process(pid)
}

fn image_of_process(pid: u32) -> Option<PathBuf> {
    if pid == 0 {
        return None;
    }
    // SAFETY: no pointer arguments; the handle is closed below.
    let process: HANDLE = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
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
    let length = usize::try_from(length).ok()?;
    ok.then(|| PathBuf::from(OsString::from_wide(&buffer[..length])))
}

/// The engine as seen from the host: finding, querying, commanding and starting it.
#[derive(Debug)]
pub struct Engine {
    exe_dir: PathBuf,
    preview_settings: Option<PathBuf>,
    /// The engine process we started last, until it has a window or has exited.
    launched: RefCell<Option<Child>>,
}

impl Engine {
    /// Works with engines in `exe_dir`. In preview mode, `preview_settings` is handed
    /// to a started engine through [`PREVIEW_CONFIG_VAR`] so both use the same file.
    pub fn new(exe_dir: &Path, preview_settings: Option<&Path>) -> Self {
        Self {
            exe_dir: exe_dir.to_path_buf(),
            preview_settings: preview_settings.map(Path::to_path_buf),
            launched: RefCell::new(None),
        }
    }

    fn expected_image(&self) -> PathBuf {
        self.exe_dir.join(EXE_NAME)
    }

    /// The engine's window: the first window with the engine's title whose process
    /// runs our engine executable. Look-alikes from other programs are skipped.
    fn find_window(&self) -> Option<HWND> {
        let title = to_wide(WINDOW_TITLE);
        let expected = self.expected_image();
        let mut after: HWND = ptr::null_mut();
        loop {
            // SAFETY: `title` is NUL-terminated; null parent and class mean "any
            // top-level window"; `after` continues the enumeration.
            after = unsafe { FindWindowExW(ptr::null_mut(), after, ptr::null(), title.as_ptr()) };
            if after.is_null() {
                return None;
            }
            if process_image(after).is_some_and(|image| same_file(&image, &expected)) {
                return Some(after);
            }
        }
    }

    /// Whether the engine is recording or selecting a region right now.
    ///
    /// An engine that does not answer within [`QUERY_TIMEOUT_MS`] (hung, or blocked by
    /// a modal dialog) also counts as busy: quitting must never pull the rug from under
    /// a running recording.
    pub fn is_busy(&self) -> bool {
        let Some(window) = self.find_window() else {
            return false;
        };
        let mut state = 0usize;
        // SAFETY: `state` outlives the call; a stale handle just makes the call fail.
        let answered = unsafe {
            SendMessageTimeoutW(
                window,
                MSG_QUERY_STATE,
                0,
                0,
                SMTO_ABORTIFHUNG,
                QUERY_TIMEOUT_MS,
                &raw mut state,
            )
        } != 0;
        busy_from_reply(answered.then_some(state))
    }

    /// Asks a running engine to shut down. Does nothing if none is running.
    pub fn request_quit(&self) {
        if let Some(window) = self.find_window() {
            // SAFETY: posting to a window handle has no memory-safety preconditions.
            unsafe { PostMessageW(window, MSG_QUIT, 0, 0) };
        }
    }

    /// Starts a capture: posts it to the running engine or launches a new one, unless
    /// an engine we launched is still starting.
    pub fn start_capture(&self, mode: CaptureMode) -> Delivery {
        let window = self.find_window();
        // Taken out of the cell, so no borrow is held across the calls below.
        let mut launched = self.launched.take().filter(|_| window.is_none());
        let launch_in_progress =
            launched.as_mut().is_some_and(|child| matches!(child.try_wait(), Ok(None)));
        let delivery = match (next_step(window.is_some(), launch_in_progress), window) {
            (Step::Post, Some(window)) => {
                // SAFETY: posting to a window handle has no memory-safety preconditions.
                let posted = unsafe { PostMessageW(window, mode.message(), 0, 0) } != 0;
                if posted { Delivery::Posted } else { Delivery::Failed }
            }
            (Step::Wait, _) => Delivery::AlreadyStarting,
            (Step::Spawn | Step::Post, _) => {
                launched = self.command(mode).spawn().ok();
                if launched.is_some() { Delivery::Started } else { Delivery::Failed }
            }
        };
        self.launched.replace(launched);
        delivery
    }

    /// `<exe_dir>\MosaikCapture.Engine.exe <flag>`, without a console window.
    fn command(&self, mode: CaptureMode) -> Command {
        let mut command = Command::new(self.expected_image());
        command.arg(mode.flag()).current_dir(&self.exe_dir).creation_flags(CREATE_NO_WINDOW);
        if let Some(settings) = &self.preview_settings {
            command.env(PREVIEW_CONFIG_VAR, settings);
        }
        command
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn engine_protocol_is_stable() {
        assert_eq!(MSG_SCREENSHOT, 0x8001);
        assert_eq!(MSG_RECORDING, 0x8002);
        assert_eq!(MSG_QUIT, 0x8003);
        assert_eq!(MSG_QUERY_STATE, 0x800A);
    }

    #[test]
    fn capture_modes_map_to_flags_and_messages() {
        assert_eq!(CaptureMode::Screenshot.flag(), "--screenshot");
        assert_eq!(CaptureMode::Recording.flag(), "--recording");
        assert_eq!(CaptureMode::Screenshot.message(), MSG_SCREENSHOT);
        assert_eq!(CaptureMode::Recording.message(), MSG_RECORDING);
    }

    #[test]
    fn busy_state_from_reply() {
        assert!(!busy_from_reply(Some(0)));
        assert!(!busy_from_reply(Some(2)), "live preview alone is not busy");
        assert!(busy_from_reply(Some(1)));
        assert!(busy_from_reply(Some(3)));
        assert!(busy_from_reply(None), "no answer counts as busy");
    }

    #[test]
    fn requests_go_to_the_engine_or_start_it_once() {
        assert_eq!(next_step(true, false), Step::Post);
        assert_eq!(next_step(true, true), Step::Post, "a window always wins");
        assert_eq!(next_step(false, true), Step::Wait, "no second engine while starting");
        assert_eq!(next_step(false, false), Step::Spawn);
        assert!(Delivery::Failed.failed());
        for delivery in [Delivery::Posted, Delivery::Started, Delivery::AlreadyStarting] {
            assert!(!delivery.failed());
        }
    }

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

    #[test]
    fn engine_command_line() {
        let engine = Engine::new(Path::new(r"C:\Apps\Mosaik"), None);
        let command = engine.command(CaptureMode::Recording);
        assert_eq!(command.get_program(), Path::new(r"C:\Apps\Mosaik\MosaikCapture.Engine.exe"));
        assert_eq!(command.get_args().collect::<Vec<_>>(), ["--recording"]);
        assert_eq!(command.get_current_dir(), Some(Path::new(r"C:\Apps\Mosaik")));
        assert_eq!(command.get_envs().count(), 0, "real settings: no override");
    }

    #[test]
    fn preview_settings_are_passed_to_the_engine() {
        let ini = Path::new(r"C:\Temp\qa.ini");
        let engine = Engine::new(Path::new(r"C:\Apps"), Some(ini));
        let command = engine.command(CaptureMode::Screenshot);
        let envs: Vec<_> = command.get_envs().collect();
        assert_eq!(envs, [(PREVIEW_CONFIG_VAR.as_ref(), Some(ini.as_os_str()))]);
    }

    #[test]
    fn missing_engine_is_a_failed_delivery() {
        let missing = std::env::temp_dir().join("mosaik-capture-no-engine-here");
        let engine = Engine::new(&missing, None);
        assert_eq!(engine.start_capture(CaptureMode::Screenshot), Delivery::Failed);
        assert!(!engine.is_busy(), "no engine in that folder");
    }
}
