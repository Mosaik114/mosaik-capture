//! Single-instance handling: a named mutex decides which process is the primary
//! instance; later processes forward their action to its window and exit. Only a
//! window whose process runs this same executable receives the action.

use std::env;
use std::io;
use std::path::{Path, PathBuf};
use std::ptr;
use std::thread;
use std::time::Duration;

use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HWND,
};
use windows_sys::Win32::System::Threading::CreateMutexW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AllowSetForegroundWindow, FindWindowExW, GetWindowThreadProcessId, SMTO_ABORTIFHUNG,
    SendMessageTimeoutW,
};

use crate::cli::Action;
use crate::ipc;
use crate::process::{process_image, same_file};
use crate::wide::to_wide;

/// Session-local mutex that marks the primary instance.
const MUTEX_NAME: &str = r"Local\MosaikCapture.Host";
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

/// Where and how patiently to forward an action.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Target {
    /// Window class of the receiving window.
    pub class: String,
    /// How often to look for the window while the primary may still be starting.
    pub attempts: u32,
    /// Pause between two lookups.
    pub interval: Duration,
    /// How long the receiver may take to handle the message.
    pub timeout: Duration,
    /// The executable the receiving process must run (compared as a file, see
    /// [`same_file`]); `None` accepts any process. A window of the right class from
    /// another program — or another copy of this app elsewhere — is not trusted.
    pub image: Option<PathBuf>,
}

impl Target {
    /// The primary instance: up to 2 s for its window to appear, 2 s to answer.
    pub fn primary() -> Self {
        Self {
            class: ipc::WINDOW_CLASS.to_owned(),
            attempts: 200,
            interval: Duration::from_millis(10),
            timeout: Duration::from_secs(2),
            // The primary runs this very executable. If Windows cannot tell us which
            // one that is, verification is skipped rather than refusing to forward.
            image: env::current_exe().ok(),
        }
    }

    /// Looks for a trusted window, retrying while it may still be created.
    fn find_window(&self) -> Option<HWND> {
        let class = to_wide(&self.class);
        (0..self.attempts).find_map(|attempt| {
            if attempt > 0 {
                thread::sleep(self.interval);
            }
            self.trusted_window(&class)
        })
    }

    /// The first window of the class whose process runs [`Target::image`].
    fn trusted_window(&self, class: &[u16]) -> Option<HWND> {
        let mut after: HWND = ptr::null_mut();
        loop {
            // SAFETY: `class` is NUL-terminated; null parent and title mean "any
            // top-level window of the class"; `after` continues the enumeration.
            after = unsafe { FindWindowExW(ptr::null_mut(), after, class.as_ptr(), ptr::null()) };
            if after.is_null() {
                return None;
            }
            let image = process_image(after);
            if is_trusted(image.as_deref(), self.image.as_deref()) {
                return Some(after);
            }
        }
    }
}

/// Whether a window whose process runs `image` may receive forwarded actions.
fn is_trusted(image: Option<&Path>, expected: Option<&Path>) -> bool {
    match (image, expected) {
        (_, None) => true,
        (Some(image), Some(expected)) => same_file(image, expected),
        (None, Some(_)) => false,
    }
}

/// Waits for the primary instance's window and sends it `action`.
pub fn forward(action: Action) -> Forwarded {
    forward_to(&Target::primary(), action)
}

/// Waits for `target`'s window and sends it `action`.
pub fn forward_to(target: &Target, action: Action) -> Forwarded {
    let Some(window) = target.find_window() else {
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
    let timeout = u32::try_from(target.timeout.as_millis()).unwrap_or(u32::MAX);
    let mut response = 0usize;
    // SAFETY: `response` outlives the call; see above for the handle.
    let delivered = unsafe {
        SendMessageTimeoutW(window, message, 0, 0, SMTO_ABORTIFHUNG, timeout, &raw mut response)
    } != 0;
    if delivered { Forwarded::Delivered(response.cast_signed()) } else { Forwarded::Failed }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;
    use std::time::Instant;
    use windows_sys::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, MSG,
        PostThreadMessageW, RegisterClassW, WM_QUIT, WNDCLASSW,
    };

    /// Answers PROBE like a healthy primary; everything else goes to the default.
    unsafe extern "system" fn fake_primary(
        window: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        if message == ipc::PROBE {
            return ipc::ProbeResponse::HEALTHY;
        }
        // SAFETY: default handling with the arguments we were given.
        unsafe { DefWindowProcW(window, message, wparam, lparam) }
    }

    /// A hidden top-level window of a test-only class, owned by its own thread. With
    /// `pump`, the thread answers messages until told to quit; without, it never does.
    struct FakePrimary {
        class: String,
        thread_id: u32,
        worker: Option<thread::JoinHandle<()>>,
    }

    impl FakePrimary {
        fn start(name: &str, pump: bool) -> Self {
            let class = format!("MosaikCapture.Test.{name}.{}", std::process::id());
            let (sender, receiver) = mpsc::channel();
            let wide_class = to_wide(&class);
            let worker = thread::spawn(move || {
                // SAFETY: a null name returns the handle of the executable itself.
                let instance = unsafe { GetModuleHandleW(ptr::null()) };
                let window_class = WNDCLASSW {
                    style: 0,
                    lpfnWndProc: Some(fake_primary),
                    cbClsExtra: 0,
                    cbWndExtra: 0,
                    hInstance: instance,
                    hIcon: ptr::null_mut(),
                    hCursor: ptr::null_mut(),
                    hbrBackground: ptr::null_mut(),
                    lpszMenuName: ptr::null(),
                    lpszClassName: wide_class.as_ptr(),
                };
                // SAFETY: the class struct and name outlive the calls; the window is
                // created without WS_VISIBLE and never shown.
                let window = unsafe {
                    RegisterClassW(&raw const window_class);
                    CreateWindowExW(
                        0,
                        wide_class.as_ptr(),
                        ptr::null(),
                        0,
                        0,
                        0,
                        0,
                        0,
                        ptr::null_mut(),
                        ptr::null_mut(),
                        instance,
                        ptr::null(),
                    )
                };
                assert!(!window.is_null());
                // SAFETY: no arguments.
                sender.send(unsafe { GetCurrentThreadId() }).unwrap();
                if pump {
                    // SAFETY: MSG is plain data; `message` outlives the calls.
                    unsafe {
                        let mut message: MSG = std::mem::zeroed();
                        while GetMessageW(&raw mut message, ptr::null_mut(), 0, 0) > 0 {
                            DispatchMessageW(&raw const message);
                        }
                    }
                } else {
                    thread::sleep(Duration::from_millis(500));
                }
                // SAFETY: destroys the window on the thread that created it.
                unsafe { DestroyWindow(window) };
            });
            let thread_id = receiver.recv().unwrap();
            Self { class, thread_id, worker: Some(worker) }
        }

        fn target(&self) -> Target {
            Target {
                class: self.class.clone(),
                attempts: 1,
                interval: Duration::ZERO,
                timeout: Duration::from_millis(100),
                // The fake primary lives in this test process.
                image: Some(std::env::current_exe().unwrap()),
            }
        }
    }

    impl Drop for FakePrimary {
        fn drop(&mut self) {
            // SAFETY: posting to a thread has no memory-safety preconditions.
            unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0) };
            if let Some(worker) = self.worker.take() {
                let _ = worker.join();
            }
        }
    }

    #[test]
    fn primary_target_waits_two_seconds_at_most() {
        let target = Target::primary();
        assert_eq!(target.class, "MosaikCapture.Host");
        assert_eq!(target.interval * target.attempts, Duration::from_secs(2));
        assert_eq!(target.timeout, Duration::from_secs(2));
    }

    #[test]
    fn missing_window_is_retried_then_reported() {
        let target = Target {
            class: format!("MosaikCapture.Test.Absent.{}", std::process::id()),
            attempts: 5,
            interval: Duration::from_millis(20),
            timeout: Duration::from_millis(100),
            image: None,
        };
        let started = Instant::now();
        assert_eq!(forward_to(&target, Action::Probe), Forwarded::NoWindow);
        let elapsed = started.elapsed();
        assert!(elapsed >= Duration::from_millis(80), "4 pauses between 5 lookups: {elapsed:?}");
        assert!(elapsed < Duration::from_secs(2), "{elapsed:?}");
    }

    #[test]
    fn answers_are_delivered() {
        let primary = FakePrimary::start("Pumping", true);
        let target = primary.target();
        let healthy = Forwarded::Delivered(ipc::ProbeResponse::HEALTHY);
        assert_eq!(forward_to(&target, Action::Probe), healthy);
        // Unknown messages reach DefWindowProc, which answers 0.
        assert_eq!(forward_to(&target, Action::Quit), Forwarded::Delivered(0));
        // `--background` needs no message at all.
        assert_eq!(forward_to(&target, Action::Background), Forwarded::Delivered(0));
    }

    #[test]
    fn windows_of_other_executables_are_ignored() {
        let primary = FakePrimary::start("Foreign", true);
        let mut target = primary.target();
        target.image = Some(std::env::temp_dir().join("MosaikCapture-elsewhere.exe"));
        assert_eq!(forward_to(&target, Action::Probe), Forwarded::NoWindow);
    }

    #[test]
    fn trust_decision() {
        let exe = std::env::current_exe().unwrap();
        let other = std::env::temp_dir();
        assert!(is_trusted(Some(&exe), Some(&exe)));
        assert!(!is_trusted(Some(&other), Some(&exe)));
        assert!(!is_trusted(None, Some(&exe)), "unknown process image");
        assert!(is_trusted(None, None), "no expectation, no check");
        assert!(Target::primary().image.is_some());
    }

    #[test]
    fn unresponsive_primary_is_a_failure() {
        let primary = FakePrimary::start("Blocked", false);
        let started = Instant::now();
        assert_eq!(forward_to(&primary.target(), Action::Probe), Forwarded::Failed);
        assert!(started.elapsed() < Duration::from_secs(2), "the timeout applies");
    }
}
