//! Mosaik Capture host: a small tray app that owns the global screenshot and recording
//! hotkeys, shows the settings window and starts the capture engine.
//!
//! Only one instance runs per session. Starting the executable again forwards the
//! command-line action (see [`cli`]) to the running instance.
//!
//! Tests: `cargo test` runs the unit tests. Two tests are `#[ignore]`d by default and
//! run in CI with `cargo test -- --include-ignored`: the end-to-end test of the real
//! settings window (`ui::tests::settings_window_end_to_end`), which creates a window
//! and registers global hotkeys, and the cross-process test against the built engine
//! (`engine::tests::quit_protocol_across_processes`), which needs `bin\` built first.

#![cfg_attr(not(test), windows_subsystem = "windows")]

mod autostart;
mod cli;
mod config;
#[cfg(test)]
mod contract;
mod engine;
mod hotkey;
mod ipc;
mod isolation;
mod process;
mod self_test;
mod single_instance;
mod store;
mod ui;
mod wide;

/// The resource serialiser of `build.rs`, compiled here so `cargo test` covers it.
#[cfg(test)]
#[path = "../build/rsrc.rs"]
mod rsrc;

use std::process::ExitCode;

use cli::{Action, Command};
use isolation::{Isolation, isolated};
use single_instance::{Forwarded, InstanceLock};

/// Process exit codes, for scripts and CI. Only outcomes a caller acts on have their
/// own code; every other failure is [`Exit::Failed`]. A panic aborts (release profile
/// `panic = "abort"`), which Windows reports as `0xC0000409`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Exit {
    /// Done; for a forwarded action: the running instance accepted it.
    Success = 0,
    /// The app could not start (mutex, window, message loop) or the running instance
    /// did not take the forwarded action.
    Failed = 1,
    SelfTestFailed = 3,
    /// `--probe`: no running instance, or one with a hotkey not registered or not answering.
    ProbeFailed = 5,
    /// `--quit` was refused: the engine is busy, a dialog of the running instance is
    /// open, or it is still starting.
    QuitRefused = 11,
}

impl From<Exit> for ExitCode {
    fn from(exit: Exit) -> Self {
        Self::from(exit as u8)
    }
}

fn main() -> ExitCode {
    match cli::parse(std::env::args_os().skip(1)) {
        Command::SelfTest => {
            if self_test::run() {
                Exit::Success
            } else {
                Exit::SelfTestFailed
            }
        }
        Command::Run(action) => run(action, Isolation::from_env().as_ref()),
    }
    .into()
}

fn run(action: Action, isolation: Option<&Isolation>) -> Exit {
    let mutex = isolated(single_instance::MUTEX_NAME, isolation);
    let Ok(lock) = InstanceLock::acquire(&mutex) else {
        return Exit::Failed;
    };
    if lock.already_running {
        let class = isolated(ipc::WINDOW_CLASS, isolation);
        return forwarded_exit(action, single_instance::forward(&class, action));
    }

    match action {
        // Nothing to quit, nothing healthy to probe.
        Action::Quit => Exit::Success,
        Action::Probe => Exit::ProbeFailed,
        _ => match ui::run(action, isolation) {
            Ok(()) => Exit::Success,
            Err(error) => {
                // At logon (`--background`) nobody is waiting for a window, so no dialog.
                if action != Action::Background {
                    ui::report(error);
                }
                Exit::Failed
            }
        },
    }
}

const fn forwarded_exit(action: Action, outcome: Forwarded) -> Exit {
    match (action, outcome) {
        (Action::Probe, Forwarded::Delivered(ipc::ProbeResponse::HEALTHY)) => Exit::Success,
        (Action::Probe, _) => Exit::ProbeFailed,
        (Action::Quit, Forwarded::Delivered(ipc::QUIT_REFUSED)) => Exit::QuitRefused,
        (_, Forwarded::Delivered(_)) => Exit::Success,
        (_, Forwarded::NoWindow | Forwarded::Failed) => Exit::Failed,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn forwarding_outcomes() {
        use ipc::ProbeResponse;
        let healthy = Forwarded::Delivered(ProbeResponse::HEALTHY);
        assert_eq!(forwarded_exit(Action::Probe, healthy), Exit::Success);
        assert_eq!(forwarded_exit(Action::Probe, Forwarded::Delivered(3)), Exit::ProbeFailed);
        assert_eq!(forwarded_exit(Action::Probe, Forwarded::Failed), Exit::ProbeFailed);
        assert_eq!(forwarded_exit(Action::Probe, Forwarded::NoWindow), Exit::ProbeFailed);
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Delivered(1)), Exit::Success);
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Delivered(0)), Exit::QuitRefused);
        assert_eq!(
            forwarded_exit(Action::ShowSettings, Forwarded::Delivered(0)),
            Exit::Success,
            "only quit has a refusal answer"
        );
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Failed), Exit::Failed);
        assert_eq!(forwarded_exit(Action::Background, Forwarded::Delivered(0)), Exit::Success);
        assert_eq!(forwarded_exit(Action::TakeScreenshot, Forwarded::NoWindow), Exit::Failed);
    }
}
