//! Single-instance handling: a named mutex decides which process is the primary
//! instance; later processes forward their action to its window and exit.

use std::io;
use std::ptr;
use std::thread;
use std::time::Duration;

use windows_sys::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE};
use windows_sys::Win32::System::Threading::CreateMutexW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AllowSetForegroundWindow, FindWindowW, GetWindowThreadProcessId, SMTO_ABORTIFHUNG,
    SendMessageTimeoutW,
};

use crate::cli::Action;
use crate::ipc;
use crate::wide::to_wide;

/// Session-local mutex that marks the primary instance.
const MUTEX_NAME: &str = r"Local\MosaikCapture.Host";
/// How often to look for the primary's window while it may still be starting up.
const FIND_ATTEMPTS: u32 = 200;
/// Pause between two lookups.
const FIND_INTERVAL: Duration = Duration::from_millis(10);
/// How long the primary instance may take to handle a forwarded action.
const FORWARD_TIMEOUT_MS: u32 = 2000;

/// The instance mutex; held for the lifetime of the process.
#[derive(Debug)]
pub struct InstanceLock {
    handle: HANDLE,
    /// Another process created the mutex first.
    pub already_running: bool,
}

impl InstanceLock {
    /// Creates (or opens) the instance mutex.
    pub fn acquire() -> io::Result<Self> {
        let name = to_wide(MUTEX_NAME);
        // SAFETY: `name` is NUL-terminated; default security attributes are allowed.
        let handle = unsafe { CreateMutexW(ptr::null(), 0, name.as_ptr()) };
        // SAFETY: reads the calling thread's last-error value, set by CreateMutexW.
        let already_running = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        Ok(Self { handle, already_running })
    }
}

impl Drop for InstanceLock {
    fn drop(&mut self) {
        // SAFETY: the handle came from CreateMutexW and is closed exactly once.
        unsafe { CloseHandle(self.handle) };
    }
}

/// Outcome of forwarding an action to the primary instance.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Forwarded {
    /// The primary's window never appeared.
    NoWindow,
    /// The primary did not handle the message (hung, timed out or gone meanwhile).
    Failed,
    /// The action was delivered (or needed no message); carries the reply.
    Delivered(isize),
}

/// Waits for the primary instance's window and sends it `action`.
pub fn forward(action: Action) -> Forwarded {
    let class = to_wide(ipc::WINDOW_CLASS);
    let window = (0..FIND_ATTEMPTS).find_map(|attempt| {
        if attempt > 0 {
            thread::sleep(FIND_INTERVAL);
        }
        // SAFETY: `class` is NUL-terminated; a null title matches any window.
        let window = unsafe { FindWindowW(class.as_ptr(), ptr::null()) };
        (!window.is_null()).then_some(window)
    });
    let Some(window) = window else {
        return Forwarded::NoWindow;
    };

    let mut pid = 0u32;
    // SAFETY: `pid` outlives the calls; the handle came from FindWindowW, and a stale
    // handle only makes these calls fail.
    unsafe {
        GetWindowThreadProcessId(window, &raw mut pid);
        // Lets the primary bring its settings window to the foreground.
        AllowSetForegroundWindow(pid);
    }

    let Some(message) = action.message() else {
        return Forwarded::Delivered(0);
    };
    let mut response = 0usize;
    // SAFETY: `response` outlives the call; see above for the handle.
    let delivered = unsafe {
        SendMessageTimeoutW(
            window,
            message,
            0,
            0,
            SMTO_ABORTIFHUNG,
            FORWARD_TIMEOUT_MS,
            &raw mut response,
        )
    } != 0;
    if delivered { Forwarded::Delivered(response.cast_signed()) } else { Forwarded::Failed }
}
