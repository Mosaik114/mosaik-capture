//! Application state and the behaviour behind every window message.
//!
//! [`App`] lives on the stack of [`super::run`] for the whole message loop and is
//! reached from the window procedure through `GWLP_USERDATA`. Window procedures are
//! re-entered (e.g. `SendMessage` inside a handler, modal dialogs and menus), so all
//! methods take `&self`; mutable state sits in `Cell`/`RefCell`, and `RefCell`
//! borrows are never held across Win32 calls.

use std::cell::{Cell, RefCell};
use std::path::PathBuf;
use std::ptr;

use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{
    BeginPaint, EndPaint, FillRect, HDC, InvalidateRect, PAINTSTRUCT, SetBkColor, SetTextColor,
};
use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, SetWindowTheme};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    MOD_NOREPEAT, RegisterHotKey, SetFocus, UnregisterHotKey,
};
use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BS_AUTOCHECKBOX, BS_OWNERDRAW, CBS_DROPDOWNLIST, CreateWindowExW, DestroyWindow,
    ES_AUTOHSCROLL, HMENU, PostQuitMessage, RegisterWindowMessageW, SHOW_WINDOW_CMD, SW_HIDE,
    SW_RESTORE, SW_SHOWNORMAL, SetForegroundWindow, ShowWindow, WINDOW_EX_STYLE, WINDOW_STYLE,
    WM_CLOSE, WM_COMMAND, WM_CTLCOLORBTN, WM_CTLCOLOREDIT, WM_CTLCOLORSTATIC, WM_DESTROY,
    WM_DRAWITEM, WM_HOTKEY, WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_PAINT, WM_RBUTTONUP, WS_CHILD,
    WS_EX_CLIENTEDGE, WS_TABSTOP, WS_VISIBLE, WS_VSCROLL,
};
use windows_sys::w;

use super::controls;
use super::folder_picker;
use super::layout::{CONTROLS, ControlKind, Dpi, EDIT_LIMIT, FontRole, id};
use super::strings::status;
use super::theme::{self, Theme};
use super::tray::{self, MenuCommand};
use crate::autostart;
use crate::cli::Action;
use crate::config::{Config, ConfigError, Countdown, FrameRate};
use crate::engine::{self, CaptureMode};
use crate::hotkey::Hotkey;
use crate::ipc::{self, AudioSettings, ProbeResponse};
use crate::store::SettingsStore;
use crate::wide::to_wide;

/// `RegisterHotKey` id of the screenshot hotkey (arrives as `WM_HOTKEY`'s `wParam`).
const HOTKEY_SCREENSHOT: i32 = 1;
/// `RegisterHotKey` id of the recording hotkey.
const HOTKEY_RECORDING: i32 = 2;

/// Everything the running app needs.
#[derive(Debug)]
pub struct App {
    window: Cell<HWND>,
    instance: HINSTANCE,
    dpi: Dpi,
    theme: Theme,
    store: SettingsStore,
    exe: PathBuf,
    exe_dir: PathBuf,
    /// Preview mode: separate settings file, autostart untouched.
    preview: bool,
    /// The saved settings (the window may show unsaved edits).
    config: RefCell<Config>,
    /// Which hotkeys are currently registered.
    hotkeys: Cell<ProbeResponse>,
    /// Whether the status line shows success (or an error).
    status_ok: Cell<bool>,
    /// Message id of Explorer's `TaskbarCreated` broadcast.
    taskbar_created: Cell<u32>,
}

impl App {
    /// Creates the state; the window is attached later by [`App::attach`].
    pub fn new(
        instance: HINSTANCE,
        dpi: Dpi,
        store: SettingsStore,
        config: Config,
        exe: PathBuf,
        preview: bool,
    ) -> Self {
        let exe_dir = exe.parent().map(PathBuf::from).unwrap_or_default();
        Self {
            window: Cell::new(ptr::null_mut()),
            instance,
            dpi,
            theme: Theme::new(dpi),
            store,
            exe,
            exe_dir,
            preview,
            config: RefCell::new(config),
            hotkeys: Cell::new(ProbeResponse::default()),
            status_ok: Cell::new(true),
            taskbar_created: Cell::new(0),
        }
    }

    /// Records the window handle (called while the window is being created).
    pub fn attach(&self, window: HWND) {
        self.window.set(window);
    }

    /// The app window.
    pub fn window(&self) -> HWND {
        self.window.get()
    }

    /// The module instance.
    pub fn instance(&self) -> HINSTANCE {
        self.instance
    }

    /// Theme objects (fonts, brushes).
    pub fn theme(&self) -> &Theme {
        &self.theme
    }

    fn child(&self, id: i32) -> HWND {
        controls::child(self.window(), id)
    }

    fn show(&self, command: SHOW_WINDOW_CMD) {
        // SAFETY: no pointer arguments.
        unsafe { ShowWindow(self.window(), command) };
    }

    /// Builds the window contents, adds the tray icon, registers the hotkeys,
    /// writes the (normalised) settings back and performs the start-up `action`.
    pub fn start(&self, action: Action) {
        self.create_controls();
        // SAFETY: the literal is NUL-terminated.
        self.taskbar_created
            .set(unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) });
        tray::add(self.window(), self.instance);

        let config = self.config.borrow().clone();
        let registered = self.register_hotkeys(&config);
        let saved = self.store.save(&config).is_ok()
            && (self.preview || autostart::set_enabled(config.autostart, &self.exe).is_ok());
        if !registered {
            self.set_status(status::HOTKEY_TAKEN, false);
        } else if !saved {
            self.set_status(status::STARTUP_SAVE_FAILED, false);
        }

        let settings_requested =
            matches!(action, Action::ShowSettings | Action::ShowRecordingSettings);
        if settings_requested || !registered {
            self.show_settings(action == Action::ShowRecordingSettings);
        } else if action == Action::TakeScreenshot {
            self.capture(CaptureMode::Screenshot);
        } else if action == Action::ToggleRecording {
            self.capture(CaptureMode::Recording);
        }
    }

    /// Handles a window message; `None` lets `DefWindowProcW` handle it.
    pub fn handle_message(&self, message: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
        let taskbar_created = self.taskbar_created.get();
        if taskbar_created != 0 && message == taskbar_created {
            // Explorer restarted: the icon has to be added again.
            tray::add(self.window(), self.instance);
            return Some(0);
        }
        let result = match message {
            WM_CLOSE => {
                self.show(SW_HIDE);
                0
            }
            WM_DESTROY => {
                self.unregister_hotkeys();
                tray::remove(self.window(), self.instance);
                // SAFETY: no arguments besides the exit code.
                unsafe { PostQuitMessage(0) };
                0
            }
            WM_PAINT => {
                self.paint_background();
                0
            }
            WM_CTLCOLORSTATIC | WM_CTLCOLORBTN => {
                let is_status = lparam as HWND == self.child(id::STATUS);
                let color = if is_status && !self.status_ok.get() {
                    theme::ERROR
                } else {
                    theme::INK
                };
                color_control(wparam as HDC, color, theme::BACKGROUND);
                self.theme.background_brush() as LRESULT
            }
            WM_CTLCOLOREDIT => {
                color_control(wparam as HDC, theme::INK, theme::FIELD);
                self.theme.field_brush() as LRESULT
            }
            WM_DRAWITEM => {
                // SAFETY: for WM_DRAWITEM, lParam points to a DRAWITEMSTRUCT that is
                // valid for the duration of the message.
                let item = unsafe { &*(lparam as *const DRAWITEMSTRUCT) };
                self.theme.draw_button(item);
                1
            }
            WM_COMMAND => {
                self.command(low_word(wparam));
                0
            }
            WM_HOTKEY => {
                match i32::try_from(wparam) {
                    Ok(HOTKEY_SCREENSHOT) => self.capture(CaptureMode::Screenshot),
                    Ok(HOTKEY_RECORDING) => self.capture(CaptureMode::Recording),
                    _ => {}
                }
                0
            }
            ipc::SHOW_SETTINGS => {
                self.show_settings(false);
                1
            }
            ipc::SHOW_RECORDING_SETTINGS => {
                self.show_settings(true);
                1
            }
            ipc::TAKE_SCREENSHOT => {
                self.capture(CaptureMode::Screenshot);
                1
            }
            ipc::TOGGLE_RECORDING => {
                self.capture(CaptureMode::Recording);
                1
            }
            ipc::QUIT => {
                self.quit();
                1
            }
            ipc::PROBE => self.hotkeys.get().to_lresult(),
            ipc::AUDIO_SETTINGS => {
                self.apply_audio_settings(AudioSettings::from_wparam(wparam));
                1
            }
            ipc::TRAY_CALLBACK => {
                self.tray_event(lparam);
                0
            }
            _ => return None,
        };
        Some(result)
    }

    fn create_controls(&self) {
        for spec in CONTROLS {
            let (class, ex_style, style): (_, WINDOW_EX_STYLE, WINDOW_STYLE) = match spec.kind {
                ControlKind::Label(_) => (w!("STATIC"), 0, 0),
                ControlKind::Edit => (
                    w!("EDIT"),
                    WS_EX_CLIENTEDGE,
                    WS_TABSTOP | ES_AUTOHSCROLL.cast_unsigned(),
                ),
                ControlKind::Button => (w!("BUTTON"), 0, WS_TABSTOP | BS_OWNERDRAW.cast_unsigned()),
                ControlKind::Checkbox => (
                    w!("BUTTON"),
                    0,
                    WS_TABSTOP | BS_AUTOCHECKBOX.cast_unsigned(),
                ),
                ControlKind::Combo(_) => (
                    w!("COMBOBOX"),
                    0,
                    WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST.cast_unsigned(),
                ),
            };
            let [x, y, width, height] = spec.rect.map(|value| self.dpi.scale(value));
            let text = to_wide(spec.text);
            // SAFETY: class and text are NUL-terminated and outlive the call; the
            // control id is passed in the HMENU slot as child windows require.
            let control = unsafe {
                CreateWindowExW(
                    ex_style,
                    class,
                    text.as_ptr(),
                    WS_CHILD | WS_VISIBLE | style,
                    x,
                    y,
                    width,
                    height,
                    self.window(),
                    spec.id as HMENU,
                    self.instance,
                    ptr::null(),
                )
            };
            let role = match spec.kind {
                ControlKind::Label(role) => role,
                _ => FontRole::Body,
            };
            controls::set_font(control, self.theme.font(role));
            match spec.kind {
                ControlKind::Edit => controls::limit_text(control, EDIT_LIMIT),
                ControlKind::Checkbox => {
                    // Without visual styles the check box honours WM_CTLCOLORSTATIC,
                    // so its label is readable on the dark background.
                    // SAFETY: both literals are NUL-terminated.
                    unsafe { SetWindowTheme(control, w!(""), w!("")) };
                }
                ControlKind::Combo(entries) => {
                    for entry in entries {
                        controls::add_entry(control, entry);
                    }
                }
                ControlKind::Label(_) | ControlKind::Button => {}
            }
        }
        self.populate();
    }

    /// Shows the saved settings in the controls (discarding unsaved edits).
    fn populate(&self) {
        let config = self.config.borrow().clone();
        controls::set_text(self.child(id::SCREENSHOT_HOTKEY), &config.screenshot_hotkey);
        controls::set_text(self.child(id::SCREENSHOT_FOLDER), &config.screenshot_folder);
        controls::set_text(self.child(id::RECORDING_HOTKEY), &config.recording_hotkey);
        controls::set_text(self.child(id::RECORDING_FOLDER), &config.recording_folder);
        self.show_audio_settings(&config);
        controls::set_checked(self.child(id::AUTOSTART), config.autostart);
        controls::select(self.child(id::FRAME_RATE), config.frame_rate.index());
        controls::select(self.child(id::COUNTDOWN), config.countdown.index());
    }

    fn show_audio_settings(&self, config: &Config) {
        controls::set_checked(self.child(id::MICROPHONE), config.microphone);
        controls::set_checked(self.child(id::SYSTEM_AUDIO), config.system_audio);
        controls::set_checked(self.child(id::SHOW_CLICKS), config.show_clicks);
    }

    /// The settings as currently entered in the window.
    fn read_controls(&self) -> Config {
        let text = |id| controls::text(self.child(id));
        let checked = |id| controls::is_checked(self.child(id));
        Config {
            screenshot_hotkey: text(id::SCREENSHOT_HOTKEY),
            recording_hotkey: text(id::RECORDING_HOTKEY),
            screenshot_folder: text(id::SCREENSHOT_FOLDER),
            recording_folder: text(id::RECORDING_FOLDER),
            microphone: checked(id::MICROPHONE),
            system_audio: checked(id::SYSTEM_AUDIO),
            show_clicks: checked(id::SHOW_CLICKS),
            autostart: checked(id::AUTOSTART),
            frame_rate: FrameRate::from_index(controls::selection(self.child(id::FRAME_RATE))),
            countdown: Countdown::from_index(controls::selection(self.child(id::COUNTDOWN))),
        }
    }

    fn show_settings(&self, focus_recording: bool) {
        self.populate();
        self.show(SW_RESTORE);
        let focus = if focus_recording {
            id::RECORDING_HOTKEY
        } else {
            id::SCREENSHOT_HOTKEY
        };
        // SAFETY: no pointer arguments.
        unsafe {
            SetForegroundWindow(self.window());
            SetFocus(self.child(focus));
        }
    }

    fn set_status(&self, text: &str, ok: bool) {
        self.status_ok.set(ok);
        let status = self.child(id::STATUS);
        controls::set_text(status, text);
        // SAFETY: a null rectangle means the whole control.
        unsafe { InvalidateRect(status, ptr::null(), 1) };
    }

    /// Registers both hotkeys of `config`. Returns `true` only if both succeeded.
    /// Invalid or identical hotkeys register nothing and keep the previous state.
    fn register_hotkeys(&self, config: &Config) -> bool {
        let Ok(hotkeys) = config.hotkeys() else {
            return false;
        };
        let register = |id: i32, hotkey: Hotkey| {
            let modifiers = hotkey.modifiers.bits() | MOD_NOREPEAT;
            // SAFETY: no pointer arguments.
            unsafe { RegisterHotKey(self.window(), id, modifiers, u32::from(hotkey.key)) != 0 }
        };
        let state = ProbeResponse {
            screenshot_hotkey: register(HOTKEY_SCREENSHOT, hotkeys.screenshot),
            recording_hotkey: register(HOTKEY_RECORDING, hotkeys.recording),
        };
        self.hotkeys.set(state);
        state.screenshot_hotkey && state.recording_hotkey
    }

    fn unregister_hotkeys(&self) {
        // SAFETY: no pointer arguments; unregistering an unknown id fails harmlessly.
        unsafe {
            UnregisterHotKey(self.window(), HOTKEY_SCREENSHOT);
            UnregisterHotKey(self.window(), HOTKEY_RECORDING);
        }
    }

    /// Re-registers the hotkeys of the saved settings after a failed change.
    fn restore_hotkeys(&self, saved: &Config) {
        self.unregister_hotkeys();
        self.register_hotkeys(saved);
    }

    /// "Speichern": validate, re-register hotkeys, update autostart, write the file.
    /// Every failure rolls back to the previous settings.
    fn save(&self) {
        let saved = self.config.borrow().clone();
        let draft = self.read_controls();
        if let Err(error) = draft.validate() {
            let message = match error {
                ConfigError::InvalidHotkey(_) => status::INVALID_HOTKEY,
                ConfigError::DuplicateHotkey => status::DUPLICATE_HOTKEY,
                ConfigError::InvalidFolder => status::INVALID_FOLDER,
            };
            self.set_status(message, false);
            return;
        }

        self.unregister_hotkeys();
        if !self.register_hotkeys(&draft) {
            self.restore_hotkeys(&saved);
            self.set_status(status::HOTKEY_TAKEN_KEEPING_OLD, false);
            return;
        }

        let stored = (self.preview || autostart::set_enabled(draft.autostart, &self.exe).is_ok())
            && self.store.save(&draft).is_ok();
        if !stored {
            self.restore_hotkeys(&saved);
            if !self.preview {
                // Best effort: the user already sees the error below.
                let _ = autostart::set_enabled(saved.autostart, &self.exe);
            }
            self.set_status(status::SAVE_FAILED, false);
            return;
        }

        *self.config.borrow_mut() = draft;
        self.set_status(status::SAVED, true);
    }

    fn command(&self, control: i32) {
        match control {
            id::SAVE => self.save(),
            id::BROWSE_SCREENSHOT_FOLDER => self.browse(id::SCREENSHOT_FOLDER),
            id::BROWSE_RECORDING_FOLDER => self.browse(id::RECORDING_FOLDER),
            id::OPEN_SCREENSHOT_FOLDER => self.open_folder(CaptureMode::Screenshot),
            id::OPEN_RECORDING_FOLDER => self.open_folder(CaptureMode::Recording),
            id::TAKE_SCREENSHOT => self.capture(CaptureMode::Screenshot),
            id::TOGGLE_RECORDING => self.capture(CaptureMode::Recording),
            _ => {}
        }
    }

    /// Lets the user pick a folder for the given edit field.
    fn browse(&self, edit: i32) {
        let edit = self.child(edit);
        if let Some(folder) = folder_picker::pick_folder(self.window(), &controls::text(edit)) {
            controls::set_text(edit, &folder);
        }
    }

    /// Opens the saved output folder in Explorer.
    fn open_folder(&self, mode: CaptureMode) {
        let folder = {
            let config = self.config.borrow();
            match mode {
                CaptureMode::Screenshot => to_wide(&config.screenshot_folder),
                CaptureMode::Recording => to_wide(&config.recording_folder),
            }
        };
        // SAFETY: verb and path are NUL-terminated; null parameters are allowed.
        unsafe {
            ShellExecuteW(
                self.window(),
                w!("open"),
                folder.as_ptr(),
                ptr::null(),
                ptr::null(),
                SW_SHOWNORMAL,
            );
        }
    }

    /// Hides the window and hands the capture to the engine.
    fn capture(&self, mode: CaptureMode) {
        self.show(SW_HIDE);
        if !engine::start_capture(mode, &self.exe_dir) {
            self.show_settings(mode == CaptureMode::Recording);
            self.set_status(status::ENGINE_FAILED, false);
        }
    }

    /// Quits app and engine, unless the engine is in the middle of a capture.
    fn quit(&self) {
        if engine::is_busy() {
            self.show_settings(true);
            self.set_status(status::BUSY, false);
            return;
        }
        engine::request_quit();
        // SAFETY: destroys our own window; WM_DESTROY then ends the message loop.
        unsafe { DestroyWindow(self.window()) };
    }

    /// Stores audio options changed in the engine's recording toolbar.
    fn apply_audio_settings(&self, audio: AudioSettings) {
        let config = {
            let mut config = self.config.borrow_mut();
            config.microphone = audio.microphone;
            config.system_audio = audio.system_audio;
            config.show_clicks = audio.show_clicks;
            config.clone()
        };
        if self.store.save(&config).is_err() {
            self.set_status(status::AUDIO_SAVE_FAILED, false);
        }
        self.show_audio_settings(&config);
    }

    fn tray_event(&self, event: LPARAM) {
        match u32::try_from(event) {
            Ok(WM_RBUTTONUP) => match tray::show_menu(self.window()) {
                Some(MenuCommand::Screenshot) => self.capture(CaptureMode::Screenshot),
                Some(MenuCommand::Recording) => self.capture(CaptureMode::Recording),
                Some(MenuCommand::Settings) => self.show_settings(false),
                Some(MenuCommand::Quit) => self.quit(),
                None => {}
            },
            Ok(WM_LBUTTONUP | WM_LBUTTONDBLCLK) => self.show_settings(false),
            _ => {}
        }
    }

    fn paint_background(&self) {
        // SAFETY: PAINTSTRUCT is a plain C struct for which all-zero is valid.
        let mut paint: PAINTSTRUCT = unsafe { std::mem::zeroed() };
        // SAFETY: called while handling WM_PAINT for our window; `paint` outlives the
        // BeginPaint/EndPaint pair.
        unsafe {
            let dc = BeginPaint(self.window(), &raw mut paint);
            FillRect(dc, &raw const paint.rcPaint, self.theme.background_brush());
            EndPaint(self.window(), &raw const paint);
        }
    }
}

/// Sets the text and background colour for a `WM_CTLCOLOR*` reply.
fn color_control(dc: HDC, text: u32, background: u32) {
    // SAFETY: `dc` is the device context passed with WM_CTLCOLOR*.
    unsafe {
        SetTextColor(dc, text);
        SetBkColor(dc, background);
    }
}

/// `LOWORD(wParam)`: the control id of a `WM_COMMAND`.
fn low_word(value: WPARAM) -> i32 {
    i32::try_from(value & 0xFFFF).unwrap_or_default()
}
