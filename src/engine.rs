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
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::ptr;
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    FindWindowExW, PostMessageW, SMTO_ABORTIFHUNG, SendMessageTimeoutW, WM_APP,
};

use crate::process::window_runs;
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
/// How long a started engine may take to show its window before it is considered
/// stuck, killed and started again.
const LAUNCH_TIMEOUT: Duration = Duration::from_secs(10);

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
    /// Post the request to the engine's window.
    Post(HWND),
    /// An engine we started is still coming up.
    Wait,
    /// Start an engine (killing a stuck one first, if any).
    Spawn,
}

/// Decides what to do with a capture request, given the engine's window (if found)
/// and how long an engine we started has been running without one (if any).
fn next_step(engine_window: Option<HWND>, starting_for: Option<Duration>) -> Step {
    match (engine_window, starting_for) {
        (Some(window), _) => Step::Post(window),
        (None, Some(elapsed)) if elapsed < LAUNCH_TIMEOUT => Step::Wait,
        (None, _) => Step::Spawn,
    }
}

/// Interprets the engine's reply to [`MSG_QUERY_STATE`]; `None` means no answer.
const fn busy_from_reply(reply: Option<usize>) -> bool {
    match reply {
        Some(state) => state & STATE_BUSY != 0,
        None => true,
    }
}

/// The engine as seen from the host: finding, querying, commanding and starting it.
#[derive(Debug)]
pub struct Engine {
    exe_dir: PathBuf,
    preview_settings: Option<PathBuf>,
    /// The engine process we started last and when, until it has a window or exited.
    launched: RefCell<Option<(Child, Instant)>>,
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
            if window_runs(after, &expected) {
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
        // A relative (or empty) folder would make CreateProcess search the working
        // directory and PATH; only ever start the engine next to this executable.
        if !self.exe_dir.is_absolute() {
            return Delivery::Failed;
        }
        let window = self.find_window();
        // Taken out of the cell, so no borrow is held across the calls below. Engines
        // that have shown their window or exited are forgotten.
        let mut launched = self.launched.take().filter(|_| window.is_none());
        if let Some((child, _)) = launched.as_mut() {
            if !matches!(child.try_wait(), Ok(None)) {
                launched = None;
            }
        }
        let starting_for = launched.as_ref().map(|(_, since)| since.elapsed());
        let delivery = match next_step(window, starting_for) {
            Step::Post(window) => {
                // SAFETY: posting to a window handle has no memory-safety preconditions.
                let posted = unsafe { PostMessageW(window, mode.message(), 0, 0) } != 0;
                if posted { Delivery::Posted } else { Delivery::Failed }
            }
            Step::Wait => Delivery::AlreadyStarting,
            Step::Spawn => {
                if let Some((mut stuck, _)) = launched.take() {
                    // Never showed its window within LAUNCH_TIMEOUT: replace it.
                    let _ = stuck.kill();
                    let _ = stuck.wait();
                }
                launched = self.command(mode).spawn().ok().map(|child| (child, Instant::now()));
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
        let window: HWND = ptr::without_provenance_mut(0x1234);
        let soon = Some(Duration::from_secs(1));
        let stuck = Some(LAUNCH_TIMEOUT);
        assert_eq!(next_step(Some(window), None), Step::Post(window));
        assert_eq!(next_step(Some(window), soon), Step::Post(window), "a window always wins");
        assert_eq!(next_step(None, soon), Step::Wait, "no second engine while starting");
        assert_eq!(next_step(None, stuck), Step::Spawn, "a stuck engine is replaced");
        assert_eq!(next_step(None, None), Step::Spawn);
        assert!(Delivery::Failed.failed());
        for delivery in [Delivery::Posted, Delivery::Started, Delivery::AlreadyStarting] {
            assert!(!delivery.failed());
        }
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
    fn engines_are_only_started_from_an_absolute_folder() {
        for folder in ["", "relative", r".\bin"] {
            let engine = Engine::new(Path::new(folder), None);
            assert_eq!(
                engine.start_capture(CaptureMode::Screenshot),
                Delivery::Failed,
                "{folder:?}"
            );
        }
    }

    #[test]
    fn missing_engine_is_a_failed_delivery() {
        let missing = std::env::temp_dir().join("mosaik-capture-no-engine-here");
        let engine = Engine::new(&missing, None);
        assert_eq!(engine.start_capture(CaptureMode::Screenshot), Delivery::Failed);
        assert!(!engine.is_busy(), "no engine in that folder");
    }
}
