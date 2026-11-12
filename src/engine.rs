//! Talking to the capture engine (`Mosaiks-Capture-Engine.exe`), which does the
//! actual screenshots and recordings.
//!
//! A running engine owns a hidden window titled [`WINDOW_TITLE`] and accepts
//! `WM_APP`-based commands. If no engine is running, it is started with the
//! capture mode on its command line.

use std::io;
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::Command;
use std::ptr;

use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    FindWindowW, PostMessageW, SMTO_ABORTIFHUNG, SendMessageTimeoutW, WM_APP,
};

use crate::wide::to_wide;

/// Title of the engine's message window.
const WINDOW_TITLE: &str = "Mosaiks Capture Engine";
/// File name of the engine, expected next to this executable.
pub const EXE_NAME: &str = "Mosaiks-Capture-Engine.exe";

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
/// How long to wait for the state query.
const QUERY_TIMEOUT_MS: u32 = 400;

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

/// The engine's window, if the engine is running.
fn find_window() -> Option<HWND> {
    let title = to_wide(WINDOW_TITLE);
    // SAFETY: `title` is NUL-terminated; a null class name matches any class.
    let window = unsafe { FindWindowW(ptr::null(), title.as_ptr()) };
    (!window.is_null()).then_some(window)
}

/// Whether the engine is recording or selecting a region right now.
pub fn is_busy() -> bool {
    let Some(window) = find_window() else {
        return false;
    };
    let mut state = 0usize;
    // SAFETY: `state` outlives the call; a stale handle just makes the call fail.
    unsafe {
        SendMessageTimeoutW(
            window,
            MSG_QUERY_STATE,
            0,
            0,
            SMTO_ABORTIFHUNG,
            QUERY_TIMEOUT_MS,
            &raw mut state,
        );
    }
    state & STATE_BUSY != 0
}

/// Asks a running engine to shut down. Does nothing if none is running.
pub fn request_quit() {
    if let Some(window) = find_window() {
        // SAFETY: posting to a window handle has no memory-safety preconditions.
        unsafe { PostMessageW(window, MSG_QUIT, 0, 0) };
    }
}

/// Starts a capture: forwards it to the running engine or launches a new engine
/// from `exe_dir`. Returns whether the request was delivered.
pub fn start_capture(mode: CaptureMode, exe_dir: &Path) -> bool {
    match find_window() {
        // SAFETY: posting to a window handle has no memory-safety preconditions.
        Some(window) => unsafe { PostMessageW(window, mode.message(), 0, 0) != 0 },
        None => spawn(mode, exe_dir).is_ok(),
    }
}

/// Launches `<exe_dir>\Mosaiks-Capture-Engine.exe <flag>` without a console window.
fn spawn(mode: CaptureMode, exe_dir: &Path) -> io::Result<()> {
    Command::new(exe_dir.join(EXE_NAME))
        .arg(mode.flag())
        .current_dir(exe_dir)
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map(drop)
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
    fn spawn_reports_missing_engine() {
        let missing = std::env::temp_dir().join("mosaik-capture-no-engine-here");
        assert!(spawn(CaptureMode::Screenshot, &missing).is_err());
    }
}
