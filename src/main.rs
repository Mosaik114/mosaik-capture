//! Mosaiks Aufnahme: a small tray app that owns the global screenshot and recording
//! hotkeys, shows the settings window and starts the capture engine.
//!
//! Only one instance runs per session. Starting the executable again forwards the
//! command-line action (see [`cli`]) to the running instance.

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

use std::process::ExitCode;

use cli::{Action, Command, Invocation};
use single_instance::{Forwarded, InstanceLock};

/// Process exit codes. Scripts (the QA tooling, the launcher) rely on these values.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Exit {
    Success = 0,
    SelfTestFailed = 3,
    MutexUnavailable = 4,
    /// `--probe` found no healthy instance.
    ProbeFailed = 5,
    /// Another instance holds the mutex but its window never appeared.
    PrimaryWithoutWindow = 6,
    WindowClassFailed = 7,
    WindowCreationFailed = 8,
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
        return match single_instance::forward(action) {
            Forwarded::NoWindow => Exit::PrimaryWithoutWindow,
            Forwarded::Delivered(response)
                if action == Action::Probe && response != ipc::ProbeResponse::HEALTHY =>
            {
                Exit::ProbeFailed
            }
            Forwarded::Delivered(_) => Exit::Success,
        };
    }

    match action {
        // Nothing to quit, nothing healthy to probe.
        Action::Quit => Exit::Success,
        Action::Probe => Exit::ProbeFailed,
        _ => match ui::run(invocation) {
            Ok(()) => Exit::Success,
            Err(ui::StartupError::RegisterClass) => Exit::WindowClassFailed,
            Err(ui::StartupError::CreateWindow) => Exit::WindowCreationFailed,
        },
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
        ];
        for (exit, code) in codes {
            assert_eq!(exit as u8, code);
        }
    }
}
