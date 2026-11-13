//! Talking to the capture engine (`MosaikCapture.Engine.exe`), which does the
//! actual screenshots and recordings.
//!
//! A running engine owns a hidden window titled [`WINDOW_TITLE`] and accepts
//! `WM_APP`-based commands. Window titles are not unique, so a window only counts as
//! the engine if its process runs `MosaikCapture.Engine.exe` from this app's folder.
//! If no engine is running, it is started with the capture mode on its command line.
//!
//! All calls run on the UI thread; the only wait is the state query in
//! [`Engine::is_busy`], capped at [`QUERY_TIMEOUT_MS`].

use std::cell::RefCell;
use std::ffi::OsString;
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

use crate::isolation::{Isolation, isolated};
use crate::process::window_runs;
use crate::wide::to_wide;

/// Title of the engine's message window.
pub const WINDOW_TITLE: &str = "MosaikCapture.Engine";
/// File name of the engine, expected next to this executable.
pub const EXE_NAME: &str = "MosaikCapture.Engine.exe";

pub const MSG_SCREENSHOT: u32 = WM_APP + 1;
/// Start or stop a recording.
pub const MSG_RECORDING: u32 = WM_APP + 2;
/// Shut down; ignored while the engine is busy.
pub const MSG_QUIT: u32 = WM_APP + 3;
/// Answered with state bits, see [`STATE_BUSY`].
pub const MSG_QUERY_STATE: u32 = WM_APP + 10;
/// State bit: something is in progress that quitting would lose (a selection,
/// countdown, recording, open editor or export).
pub const STATE_BUSY: usize = 1;
/// Longest the UI thread waits for the state query.
const QUERY_TIMEOUT_MS: u32 = 400;
/// How long a started engine may take to show its window before it is considered
/// stuck, killed and started again.
const LAUNCH_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureMode {
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

/// Sends `message` and waits at most [`QUERY_TIMEOUT_MS`] for the reply.
fn send(window: HWND, message: u32, wparam: usize) -> Option<usize> {
    let mut reply = 0usize;
    // SAFETY: `reply` outlives the call; a stale handle just makes the call fail.
    let answered = unsafe {
        SendMessageTimeoutW(
            window,
            message,
            wparam,
            0,
            SMTO_ABORTIFHUNG,
            QUERY_TIMEOUT_MS,
            &raw mut reply,
        )
    } != 0;
    answered.then_some(reply)
}

/// The engine as seen from the host: finding, querying, commanding and starting it.
#[derive(Debug)]
pub struct Engine {
    exe_dir: PathBuf,
    title: String,
    /// Test isolation passed on to a started engine.
    env: Vec<(&'static str, OsString)>,
    /// The engine process we started last and when, until it has a window or exited.
    launched: RefCell<Option<(Child, Instant)>>,
}

impl Engine {
    /// Works with engines in `exe_dir`; an isolated host only ever sees and starts an
    /// isolated engine.
    pub fn new(exe_dir: &Path, isolation: Option<&Isolation>) -> Self {
        Self {
            exe_dir: exe_dir.to_path_buf(),
            title: isolated(WINDOW_TITLE, isolation),
            env: isolation.map(|isolation| isolation.engine_env().to_vec()).unwrap_or_default(),
            launched: RefCell::new(None),
        }
    }

    fn expected_image(&self) -> PathBuf {
        self.exe_dir.join(EXE_NAME)
    }

    /// The first window with the engine's title whose process runs our engine
    /// executable. Look-alikes from other programs are skipped.
    fn find_window(&self) -> Option<HWND> {
        let title = to_wide(&self.title);
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

    /// Whether quitting the engine now would lose work.
    ///
    /// An engine that does not answer within [`QUERY_TIMEOUT_MS`] (hung, or blocked by
    /// a modal dialog) also counts as busy: quitting must never pull the rug from under
    /// a running recording.
    pub fn is_busy(&self) -> bool {
        self.find_window().is_some_and(|window| busy_from_reply(send(window, MSG_QUERY_STATE, 0)))
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
                launched =
                    self.command(mode.flag()).spawn().ok().map(|child| (child, Instant::now()));
                if launched.is_some() { Delivery::Started } else { Delivery::Failed }
            }
        };
        self.launched.replace(launched);
        delivery
    }

    /// `<exe_dir>\MosaikCapture.Engine.exe <argument>`, without a console window.
    fn command(&self, argument: &str) -> Command {
        let mut command = Command::new(self.expected_image());
        command.arg(argument).current_dir(&self.exe_dir).creation_flags(CREATE_NO_WINDOW);
        command.envs(self.env.iter().map(|(name, value)| (name, value)));
        command
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract;
    use crate::store::SettingsStore;
    use std::{env, fs, thread};

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
        let command = engine.command(CaptureMode::Recording.flag());
        assert_eq!(command.get_program(), Path::new(r"C:\Apps\Mosaik\MosaikCapture.Engine.exe"));
        assert_eq!(command.get_args().collect::<Vec<_>>(), ["--recording"]);
        assert_eq!(command.get_current_dir(), Some(Path::new(r"C:\Apps\Mosaik")));
        assert_eq!(command.get_envs().count(), 0, "real settings: no override");
        assert_eq!(engine.title, WINDOW_TITLE);
    }

    #[test]
    fn an_isolated_engine_gets_the_isolation_variables() {
        let isolation = Isolation::for_test("unit", &env::temp_dir());
        let engine = Engine::new(Path::new(r"C:\Apps"), Some(&isolation));
        assert_eq!(engine.title, "MosaikCapture.Engine.Test.unit");
        let envs: Vec<_> = engine.command("--idle").get_envs().map(|(k, _)| k.to_owned()).collect();
        assert_eq!(envs, ["MOSAIK_CAPTURE_TEST_CONFIG", "MOSAIK_CAPTURE_TEST_INSTANCE"]);
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
        let missing = env::temp_dir().join("mosaik-capture-no-engine-here");
        let engine = Engine::new(&missing, None);
        assert_eq!(engine.start_capture(CaptureMode::Screenshot), Delivery::Failed);
        assert!(!engine.is_busy(), "no engine in that folder");
    }

    /// Kills the engine (if still running) and removes its isolated data folder.
    struct Running(Child, PathBuf);

    impl Drop for Running {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
            let _ = fs::remove_dir_all(&self.1);
        }
    }

    fn wait_for<T>(timeout: Duration, mut probe: impl FnMut() -> Option<T>) -> Option<T> {
        let started = Instant::now();
        loop {
            if let Some(value) = probe() {
                return Some(value);
            }
            if started.elapsed() > timeout {
                return None;
            }
            thread::sleep(Duration::from_millis(50));
        }
    }

    /// The quit protocol between two real processes: this host code against the
    /// built engine (`bin\MosaikCapture.Engine.exe`, or `MOSAIK_CAPTURE_ENGINE_DIR`),
    /// started `--idle` under test-only names, with settings and data in a temp folder.
    /// The engine reports idle and quits; told to simulate work (the contract's
    /// test-only message, which counts toward the engine's own busy check) it reports
    /// busy and refuses to quit through its real quit path.
    ///
    /// Ignored by default because it needs the engine built and starts a process; CI
    /// builds `bin\` and runs it with `--include-ignored`.
    #[test]
    #[ignore = "starts the built engine from bin\\; CI runs it with --include-ignored"]
    fn quit_protocol_across_processes() {
        let contract = contract::load();
        let dir = env::var_os("MOSAIK_CAPTURE_ENGINE_DIR")
            .map_or_else(|| Path::new(env!("CARGO_MANIFEST_DIR")).join("bin"), PathBuf::from);
        let exe = dir.join(EXE_NAME);
        assert!(
            exe.is_file(),
            "{} is missing: build it first (dotnet build engine/MosaikCapture.Engine.csproj -c Release -o bin)",
            exe.display()
        );

        // The process id keeps parallel runs (and leftovers of a crashed run) apart.
        let instance = format!("rust-{}", std::process::id());
        let isolation = Isolation::for_test(&instance, &env::temp_dir());
        let folders = crate::store::known_default_folders();
        SettingsStore::at(isolation.settings.clone())
            .save(&crate::config::Config::defaults(&folders))
            .unwrap();
        let engine = Engine::new(&dir, Some(&isolation));
        let child = engine.command(contract.str(&["engine", "arguments", "idle"])).spawn().unwrap();
        let mut running = Running(child, isolation.local_app_data);

        let window = wait_for(Duration::from_secs(30), || engine.find_window())
            .expect("the isolated engine's window appears");
        assert_ne!(
            Engine::new(&dir, None).find_window(),
            Some(window),
            "the real engine's name never finds a test engine"
        );
        let busy_bit = usize::try_from(contract.int(&["engine", "state_bits", "busy"])).unwrap();
        let simulate = WM_APP
            + u32::try_from(contract.int(&["test_isolation", "simulate_busy_message"])).unwrap();

        assert_eq!(send(window, MSG_QUERY_STATE, 0), Some(0), "idle after --idle");
        assert!(!engine.is_busy());

        assert_eq!(send(window, simulate, 1), Some(1));
        assert_eq!(send(window, MSG_QUERY_STATE, 0).map(|state| state & busy_bit), Some(busy_bit));
        assert!(engine.is_busy());
        engine.request_quit();
        thread::sleep(Duration::from_millis(1500));
        assert!(running.0.try_wait().unwrap().is_none(), "a busy engine refuses to quit");
        assert!(engine.is_busy(), "and still answers");

        assert_eq!(send(window, simulate, 0), Some(1));
        assert!(!engine.is_busy());
        engine.request_quit();
        let status = wait_for(Duration::from_secs(15), || running.0.try_wait().unwrap())
            .expect("an idle engine quits");
        assert!(status.success(), "{status:?}");
        assert!(engine.find_window().is_none());
    }
}
