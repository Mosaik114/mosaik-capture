//! Mosaik Capture host: a small tray app that owns the global screenshot and recording
//! hotkeys, shows the settings window and starts the capture engine.
//!
//! Only one instance runs per session. Starting the executable again forwards the
//! command-line action (see [`cli`]) to the running instance.
//!
//! Tests: `cargo test` runs the unit tests. The end-to-end test of the real settings
//! window (`ui::tests::settings_window_end_to_end`) creates a window and registers
//! global hotkeys, so it only runs when `MOSAIK_UI_TESTS=1` is set; CI sets it.

#![cfg_attr(not(test), windows_subsystem = "windows")]

mod autostart;
mod cli;
mod config;
mod engine;
mod hotkey;
mod ipc;
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

use cli::{Action, Command, Invocation};
use single_instance::{Forwarded, InstanceLock};

/// Process exit codes. Scripts (the build and QA tooling, the launcher) rely on these
/// values; 1 and 2 are left to the Rust runtime.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Exit {
    /// Done; for a forwarded action: the running instance accepted it.
    Success = 0,
    /// `--self-test` found a failing check.
    SelfTestFailed = 3,
    /// The single-instance mutex could not be created.
    MutexUnavailable = 4,
    /// `--probe` found no healthy instance (none running, or a hotkey not registered).
    ProbeFailed = 5,
    /// Another instance holds the mutex but its window never appeared.
    PrimaryWithoutWindow = 6,
    /// The window class could not be registered.
    WindowClassFailed = 7,
    /// The window or one of its controls could not be created.
    WindowCreationFailed = 8,
    /// The running instance did not answer the forwarded action (hung or exiting).
    ForwardFailed = 9,
    /// The message loop failed (`GetMessageW` returned -1).
    MessageLoopFailed = 10,
    /// `--quit` was refused: the engine is recording or selecting a region, a dialog
    /// of the running instance is open, or it is still starting.
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
        Command::Run(invocation) => run(&invocation),
    }
    .into()
}

fn run(invocation: &Invocation) -> Exit {
    let Ok(lock) = InstanceLock::acquire() else {
        return Exit::MutexUnavailable;
    };
    let action = invocation.action;

    if lock.already_running {
        return forwarded_exit(action, single_instance::forward(action));
    }

    match action {
        // Nothing to quit, nothing healthy to probe.
        Action::Quit => Exit::Success,
        Action::Probe => Exit::ProbeFailed,
        _ => match ui::run(invocation) {
            Ok(()) => Exit::Success,
            Err(ui::UiError::RegisterClass) => Exit::WindowClassFailed,
            Err(ui::UiError::CreateWindow | ui::UiError::CreateControls) => {
                Exit::WindowCreationFailed
            }
            Err(ui::UiError::MessageLoop) => Exit::MessageLoopFailed,
        },
    }
}

/// Maps the outcome of forwarding `action` to the running instance to an exit code.
fn forwarded_exit(action: Action, outcome: Forwarded) -> Exit {
    match outcome {
        Forwarded::NoWindow => Exit::PrimaryWithoutWindow,
        Forwarded::Failed => Exit::ForwardFailed,
        Forwarded::Delivered(response)
            if action == Action::Probe && response != ipc::ProbeResponse::HEALTHY =>
        {
            Exit::ProbeFailed
        }
        Forwarded::Delivered(ipc::QUIT_REFUSED) if action == Action::Quit => Exit::QuitRefused,
        Forwarded::Delivered(_) => Exit::Success,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exit_codes_are_stable() {
        let codes = [
            (Exit::Success, 0),
            (Exit::SelfTestFailed, 3),
            (Exit::MutexUnavailable, 4),
            (Exit::ProbeFailed, 5),
            (Exit::PrimaryWithoutWindow, 6),
            (Exit::WindowClassFailed, 7),
            (Exit::WindowCreationFailed, 8),
            (Exit::ForwardFailed, 9),
            (Exit::MessageLoopFailed, 10),
            (Exit::QuitRefused, 11),
        ];
        for (exit, code) in codes {
            assert_eq!(exit as u8, code);
        }
    }

    #[test]
    fn forwarding_outcomes() {
        use ipc::ProbeResponse;
        let healthy = Forwarded::Delivered(ProbeResponse::HEALTHY);
        assert_eq!(forwarded_exit(Action::Probe, healthy), Exit::Success);
        assert_eq!(forwarded_exit(Action::Probe, Forwarded::Delivered(3)), Exit::ProbeFailed);
        assert_eq!(forwarded_exit(Action::Probe, Forwarded::Failed), Exit::ForwardFailed);
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Delivered(1)), Exit::Success);
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Delivered(0)), Exit::QuitRefused);
        assert_eq!(
            forwarded_exit(Action::ShowSettings, Forwarded::Delivered(0)),
            Exit::Success,
            "only quit has a refusal answer"
        );
        assert_eq!(forwarded_exit(Action::Quit, Forwarded::Failed), Exit::ForwardFailed);
        assert_eq!(forwarded_exit(Action::Background, Forwarded::Delivered(0)), Exit::Success);
        assert_eq!(
            forwarded_exit(Action::TakeScreenshot, Forwarded::NoWindow),
            Exit::PrimaryWithoutWindow
        );
    }
}
