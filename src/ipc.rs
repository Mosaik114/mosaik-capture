//! The message protocol of the app window.
//!
//! Other processes find the window by its class name and send these `WM_APP`-based
//! messages: a second instance forwards its command-line action, and the capture
//! engine reports audio changes. The numbers are a public contract and must not change.

use windows_sys::Win32::UI::WindowsAndMessaging::WM_APP;

/// Window class of the app window, looked up with `FindWindowW`.
pub const WINDOW_CLASS: &str = "MosaikCapture.Host";

/// Show the settings window, focusing the screenshot hotkey.
pub const SHOW_SETTINGS: u32 = WM_APP + 1;
/// Show the settings window, focusing the recording hotkey.
pub const SHOW_RECORDING_SETTINGS: u32 = WM_APP + 2;
/// Take a screenshot.
pub const TAKE_SCREENSHOT: u32 = WM_APP + 3;
/// Start or stop a recording.
pub const TOGGLE_RECORDING: u32 = WM_APP + 4;
/// Quit the app. Answered with [`QUIT_ACCEPTED`], or [`QUIT_REFUSED`] while the
/// engine is busy, a dialog is open or start-up is not finished.
pub const QUIT: u32 = WM_APP + 9;
/// Reply to [`QUIT`]: the app is shutting down.
pub const QUIT_ACCEPTED: isize = 1;
/// Reply to [`QUIT`]: the app keeps running.
pub const QUIT_REFUSED: isize = 0;
/// Liveness probe; answered with a [`ProbeResponse`] bit set.
pub const PROBE: u32 = WM_APP + 10;
/// Sent by the engine: new audio settings in `wParam` (see [`AudioSettings`]).
pub const AUDIO_SETTINGS: u32 = WM_APP + 20;
/// Notification-area icon callback.
pub const TRAY_CALLBACK: u32 = WM_APP + 30;

/// Whether `message` is a request from outside (another instance, the engine or the
/// tray) that may only be handled once start-up has finished.
///
/// While the app starts, some calls (e.g. `Shell_NotifyIconW`) dispatch incoming sent
/// messages. Such requests are answered with 0 and otherwise ignored, so a `--probe`
/// during start-up reports "not ready" instead of seeing half-initialised state.
pub const fn needs_startup(message: u32) -> bool {
    matches!(
        message,
        SHOW_SETTINGS
            | SHOW_RECORDING_SETTINGS
            | TAKE_SCREENSHOT
            | TOGGLE_RECORDING
            | QUIT
            | PROBE
            | AUDIO_SETTINGS
            | TRAY_CALLBACK
    )
}

/// Reply to [`PROBE`]: which parts of the app are working.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ProbeResponse {
    /// The screenshot hotkey is registered.
    pub screenshot_hotkey: bool,
    /// The recording hotkey is registered.
    pub recording_hotkey: bool,
}

impl ProbeResponse {
    const ALIVE: isize = 1;
    const SCREENSHOT_HOTKEY: isize = 2;
    const RECORDING_HOTKEY: isize = 4;
    /// The answer of a fully working instance.
    pub const HEALTHY: isize = Self::ALIVE | Self::SCREENSHOT_HOTKEY | Self::RECORDING_HOTKEY;

    /// Encodes the reply as the message result.
    pub const fn to_lresult(self) -> isize {
        let mut bits = Self::ALIVE;
        if self.screenshot_hotkey {
            bits |= Self::SCREENSHOT_HOTKEY;
        }
        if self.recording_hotkey {
            bits |= Self::RECORDING_HOTKEY;
        }
        bits
    }
}

/// Audio options carried in the `wParam` of [`AUDIO_SETTINGS`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AudioSettings {
    /// Record the microphone.
    pub microphone: bool,
    /// Record system audio.
    pub system_audio: bool,
    /// Visualise mouse clicks.
    pub show_clicks: bool,
}

impl AudioSettings {
    const MICROPHONE: usize = 1;
    const SYSTEM_AUDIO: usize = 2;
    const SHOW_CLICKS: usize = 4;

    /// Decodes the message parameter.
    pub const fn from_wparam(wparam: usize) -> Self {
        Self {
            microphone: wparam & Self::MICROPHONE != 0,
            system_audio: wparam & Self::SYSTEM_AUDIO != 0,
            show_clicks: wparam & Self::SHOW_CLICKS != 0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requests_wait_for_startup() {
        for message in [
            SHOW_SETTINGS,
            SHOW_RECORDING_SETTINGS,
            TAKE_SCREENSHOT,
            TOGGLE_RECORDING,
            QUIT,
            PROBE,
            AUDIO_SETTINGS,
            TRAY_CALLBACK,
        ] {
            assert!(needs_startup(message), "{message:#x}");
        }
        for message in [WM_APP, WM_APP + 5, WM_APP + 11, 0x0010 /* WM_CLOSE */] {
            assert!(!needs_startup(message), "{message:#x}");
        }
    }

    #[test]
    fn message_numbers_are_stable() {
        let expected = [
            (SHOW_SETTINGS, 0x8001),
            (SHOW_RECORDING_SETTINGS, 0x8002),
            (TAKE_SCREENSHOT, 0x8003),
            (TOGGLE_RECORDING, 0x8004),
            (QUIT, 0x8009),
            (PROBE, 0x800A),
            (AUDIO_SETTINGS, 0x8014),
            (TRAY_CALLBACK, 0x801E),
        ];
        for (actual, value) in expected {
            assert_eq!(actual, value);
        }
    }

    #[test]
    fn probe_response_bits() {
        assert_eq!(ProbeResponse::default().to_lresult(), 1);
        let screenshot_only = ProbeResponse { screenshot_hotkey: true, recording_hotkey: false };
        assert_eq!(screenshot_only.to_lresult(), 3);
        let recording_only = ProbeResponse { screenshot_hotkey: false, recording_hotkey: true };
        assert_eq!(recording_only.to_lresult(), 5);
        let both = ProbeResponse { screenshot_hotkey: true, recording_hotkey: true };
        assert_eq!(both.to_lresult(), ProbeResponse::HEALTHY);
        assert_eq!(ProbeResponse::HEALTHY, 7);
    }

    #[test]
    fn audio_settings_bits() {
        let none = AudioSettings::from_wparam(0);
        assert!(!none.microphone && !none.system_audio && !none.show_clicks);
        let all = AudioSettings::from_wparam(7);
        assert!(all.microphone && all.system_audio && all.show_clicks);
        assert_eq!(
            AudioSettings::from_wparam(2),
            AudioSettings { microphone: false, system_audio: true, show_clicks: false }
        );
        assert_eq!(AudioSettings::from_wparam(8), none, "unknown bits are ignored");
    }
}
