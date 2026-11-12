//! Command-line parsing.
//!
//! | Argument          | Effect                                                  |
//! |-------------------|---------------------------------------------------------|
//! | *(none)*          | open the settings window                                |
//! | `recording`       | open the settings window, focused on the recording key  |
//! | `--background`    | start silently in the notification area                 |
//! | `--screenshot`    | take a screenshot                                       |
//! | `--recording`     | start or stop a recording                               |
//! | `--quit`          | quit the running instance                               |
//! | `--probe`         | exit with 0 only if a healthy instance is running       |
//! | `--preview <ini>` | use another settings file and leave autostart alone     |
//! | `--self-test`     | run the built-in checks and exit                        |
//!
//! When several actions are given, the last one wins. Unknown arguments are ignored.

use std::ffi::OsString;
use std::path::PathBuf;

use crate::ipc;

/// What the process should do.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Command {
    /// Run the built-in self-test and exit.
    SelfTest,
    /// Run normally (or forward the action to the running instance).
    Run(Invocation),
}

/// A normal start: the requested action plus options.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Invocation {
    /// The requested action.
    pub action: Action,
    /// Settings file for preview mode; `None` uses the real settings.
    pub preview: Option<PathBuf>,
}

/// The action requested on the command line.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Action {
    /// Start without showing anything.
    Background,
    /// Show the settings window.
    #[default]
    ShowSettings,
    /// Show the settings window with the recording hotkey focused.
    ShowRecordingSettings,
    /// Take a screenshot.
    TakeScreenshot,
    /// Start or stop a recording.
    ToggleRecording,
    /// Quit the running instance.
    Quit,
    /// Check that a healthy instance is running.
    Probe,
}

impl Action {
    /// The message that forwards this action to an already running instance.
    pub const fn message(self) -> Option<u32> {
        match self {
            Self::Background => None,
            Self::ShowSettings => Some(ipc::SHOW_SETTINGS),
            Self::ShowRecordingSettings => Some(ipc::SHOW_RECORDING_SETTINGS),
            Self::TakeScreenshot => Some(ipc::TAKE_SCREENSHOT),
            Self::ToggleRecording => Some(ipc::TOGGLE_RECORDING),
            Self::Quit => Some(ipc::QUIT),
            Self::Probe => Some(ipc::PROBE),
        }
    }

    fn from_arg(arg: &OsString) -> Option<Self> {
        Some(match arg.to_str()? {
            "--background" => Self::Background,
            "--screenshot" => Self::TakeScreenshot,
            "--recording" => Self::ToggleRecording,
            "--quit" => Self::Quit,
            "--probe" => Self::Probe,
            "recording" => Self::ShowRecordingSettings,
            _ => return None,
        })
    }
}

/// Parses the arguments that follow the program name.
pub fn parse(args: impl IntoIterator<Item = OsString>) -> Command {
    let mut invocation = Invocation { action: Action::default(), preview: None };
    let mut args = args.into_iter();
    while let Some(arg) = args.next() {
        if arg == "--self-test" {
            return Command::SelfTest;
        }
        if arg == "--preview" {
            if let Some(path) = args.next() {
                invocation.preview = Some(PathBuf::from(path));
            }
        } else if let Some(action) = Action::from_arg(&arg) {
            invocation.action = action;
        }
    }
    Command::Run(invocation)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(args: &[&str]) -> Command {
        parse(args.iter().map(OsString::from))
    }

    fn action(args: &[&str]) -> Action {
        match run(args) {
            Command::Run(invocation) => invocation.action,
            Command::SelfTest => panic!("unexpected self-test for {args:?}"),
        }
    }

    #[test]
    fn no_arguments_show_settings() {
        assert_eq!(
            run(&[]),
            Command::Run(Invocation { action: Action::ShowSettings, preview: None })
        );
    }

    #[test]
    fn each_action_flag() {
        assert_eq!(action(&["--background"]), Action::Background);
        assert_eq!(action(&["--screenshot"]), Action::TakeScreenshot);
        assert_eq!(action(&["--recording"]), Action::ToggleRecording);
        assert_eq!(action(&["--quit"]), Action::Quit);
        assert_eq!(action(&["--probe"]), Action::Probe);
        assert_eq!(action(&["recording"]), Action::ShowRecordingSettings);
    }

    #[test]
    fn last_action_wins_and_unknown_args_are_ignored() {
        assert_eq!(action(&["--screenshot", "--recording"]), Action::ToggleRecording);
        assert_eq!(action(&["--probe", "--whatever", "--background"]), Action::Background);
        assert_eq!(action(&["--Background", "RECORDING"]), Action::ShowSettings);
    }

    #[test]
    fn self_test_wins_anywhere() {
        assert_eq!(run(&["--self-test"]), Command::SelfTest);
        assert_eq!(run(&["--quit", "--self-test"]), Command::SelfTest);
        assert_eq!(run(&["--preview", "a.ini", "--self-test"]), Command::SelfTest);
    }

    #[test]
    fn preview_takes_a_path() {
        let Command::Run(invocation) = run(&["--preview", r"C:\tmp\qa.ini", "--background"]) else {
            panic!("expected a normal run");
        };
        assert_eq!(invocation.preview, Some(PathBuf::from(r"C:\tmp\qa.ini")));
        assert_eq!(invocation.action, Action::Background);
    }

    #[test]
    fn preview_value_is_not_parsed_as_flag() {
        let Command::Run(invocation) = run(&["--preview", "recording"]) else {
            panic!("expected a normal run");
        };
        assert_eq!(invocation.preview, Some(PathBuf::from("recording")));
        assert_eq!(invocation.action, Action::ShowSettings);
    }

    #[test]
    fn preview_without_path_is_ignored() {
        assert_eq!(
            run(&["--preview"]),
            Command::Run(Invocation { action: Action::ShowSettings, preview: None })
        );
    }

    #[test]
    fn forwarded_messages() {
        assert_eq!(Action::Background.message(), None);
        assert_eq!(Action::ShowSettings.message(), Some(ipc::SHOW_SETTINGS));
        assert_eq!(Action::ShowRecordingSettings.message(), Some(ipc::SHOW_RECORDING_SETTINGS));
        assert_eq!(Action::TakeScreenshot.message(), Some(ipc::TAKE_SCREENSHOT));
        assert_eq!(Action::ToggleRecording.message(), Some(ipc::TOGGLE_RECORDING));
        assert_eq!(Action::Quit.message(), Some(ipc::QUIT));
        assert_eq!(Action::Probe.message(), Some(ipc::PROBE));
    }
}
