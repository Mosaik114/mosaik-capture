//! Application state and the behaviour behind every window message.
//!
//! [`App`] is boxed by [`super::run`] for the whole message loop (so its address is
//! stable) and reached from the window procedure through `GWLP_USERDATA`. Window
//! procedures are re-entered (e.g. `SendMessage` inside a handler, modal dialogs and
//! menus), so all methods take `&self`; mutable state sits in `Cell`/`RefCell`, and
//! `RefCell` borrows are not held across Win32 calls that can dispatch messages to
//! this window. The one deliberate exception is painting a button, which keeps the
//! GDI objects borrowed: drawing calls (and `WM_GETTEXT` to the button itself) never
//! re-enter this window procedure.
//!
//! Session end: `WM_QUERYENDSESSION` is left to the default (logoff is never
//! blocked). On `WM_ENDSESSION` the hotkeys and the tray icon are released; settings
//! need no saving because every change is written immediately, and the engine
//! handles the session end of its own process.

use std::cell::{Cell, RefCell};
use std::env;
use std::ffi::OsString;
use std::io;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::ptr;

use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{
    BeginPaint, EndPaint, FillRect, HDC, InvalidateRect, PAINTSTRUCT, SetBkColor, SetTextColor,
};
use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, SetWindowTheme};
use windows_sys::Win32::UI::HiDpi::GetDpiForSystem;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    MOD_NOREPEAT, RegisterHotKey, SetFocus, UnregisterHotKey,
};
use windows_sys::Win32::UI::WindowsAndMessaging::BN_CLICKED;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BS_AUTOCHECKBOX, BS_OWNERDRAW, CBS_DROPDOWNLIST, DestroyWindow, ES_AUTOHSCROLL, IDCANCEL, IDOK,
    IsWindow, IsWindowVisible, KillTimer, PostQuitMessage, RegisterWindowMessageW, SHOW_WINDOW_CMD,
    SM_CXSMICON, SW_HIDE, SW_RESTORE, SWP_NOACTIVATE, SWP_NOZORDER, SetForegroundWindow, SetTimer,
    SetWindowPos, ShowWindow, WINDOW_EX_STYLE, WINDOW_STYLE, WM_CLOSE, WM_COMMAND, WM_CTLCOLORBTN,
    WM_CTLCOLOREDIT, WM_CTLCOLORSTATIC, WM_DESTROY, WM_DPICHANGED, WM_DRAWITEM, WM_ENDSESSION,
    WM_ERASEBKGND, WM_HOTKEY, WM_PAINT, WM_TIMER, WS_EX_CLIENTEDGE, WS_TABSTOP, WS_VSCROLL,
};
use windows_sys::w;

use super::controls::{Control, Creation};
use super::folder_picker;
use super::icons::{OwnedIcon, WindowIcons};
use super::layout::{CONTROLS, ControlKind, ControlSpec, Dpi, EDIT_LIMIT, id};
use super::strings::status;
use super::theme::{self, Brushes, Scaled};
use super::tray::{self, MenuCommand, TrayEvent, TrayState};
use super::{UiError, UiErrorKind};
use crate::autostart;
use crate::cli::Action;
use crate::config::{Config, ConfigError, Countdown, FrameRate};
use crate::engine::{CaptureMode, Engine};
use crate::hotkey::Hotkey;
use crate::hotkey::ParseHotkeyError;
use crate::ipc::{self, AudioSettings, ProbeResponse};
use crate::store::SettingsStore;

/// `RegisterHotKey` id of the screenshot hotkey (arrives as `WM_HOTKEY`'s `wParam`).
const HOTKEY_SCREENSHOT: i32 = 1;
/// `RegisterHotKey` id of the recording hotkey.
const HOTKEY_RECORDING: i32 = 2;
/// Window-position flags for moving and resizing without side effects.
const REPOSITION: u32 = SWP_NOZORDER | SWP_NOACTIVATE;
/// Timer that retries adding the tray icon while the taskbar is not ready.
const TRAY_RETRY_TIMER: usize = 1;
/// Interval of [`TRAY_RETRY_TIMER`].
const TRAY_RETRY_MS: u32 = 2000;
/// How often adding the tray icon is retried (about a minute in total).
const TRAY_RETRIES: u32 = 30;

/// Everything the running app needs.
#[derive(Debug)]
pub struct App {
    window: Cell<HWND>,
    instance: HINSTANCE,
    /// DPI of the monitor the window is on.
    dpi: Cell<Dpi>,
    /// Fonts, button brushes and pens for the current DPI.
    scaled: RefCell<Scaled>,
    brushes: Brushes,
    /// Caption and taskbar icons for the current DPI.
    window_icons: RefCell<Option<WindowIcons>>,
    /// Notification-area icon, sized for the taskbar (system DPI).
    tray_icon: RefCell<OwnedIcon>,
    /// Remaining attempts to add the tray icon.
    tray_retries: Cell<u32>,
    /// How far the tray icon has been set up.
    tray_state: Cell<TrayState>,
    /// Every created control with its layout entry, for re-layout on DPI changes.
    children: RefCell<Vec<(Control, &'static ControlSpec)>>,
    store: SettingsStore,
    /// This executable, for the autostart entry; `None` if Windows could not tell.
    exe: Option<PathBuf>,
    engine: Engine,
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
    /// Start-up has finished; requests from outside are handled only from then on.
    started: Cell<bool>,
    /// Number of modal loops (folder picker, tray menu) currently running.
    modal_depth: Cell<u32>,
    /// A capture requested while a modal loop ran; started when it ends.
    pending_capture: Cell<Option<CaptureMode>>,
}

impl App {
    /// Creates the state; the window is attached later by [`App::attach`].
    /// `preview` is the settings file of preview mode, if any.
    pub fn new(
        instance: HINSTANCE,
        dpi: Dpi,
        store: SettingsStore,
        config: Config,
        exe: Option<PathBuf>,
        preview: Option<&Path>,
    ) -> Self {
        let exe_dir = exe.as_deref().and_then(Path::parent).map(PathBuf::from).unwrap_or_default();
        Self {
            window: Cell::new(ptr::null_mut()),
            instance,
            dpi: Cell::new(dpi),
            scaled: RefCell::new(Scaled::new(dpi)),
            brushes: Brushes::new(),
            window_icons: RefCell::new(None),
            // SAFETY: no arguments.
            tray_icon: RefCell::new(OwnedIcon::load(
                instance,
                SM_CXSMICON,
                Dpi::new(unsafe { GetDpiForSystem() }),
            )),
            tray_retries: Cell::new(TRAY_RETRIES),
            tray_state: Cell::new(TrayState::Missing),
            children: RefCell::new(Vec::new()),
            store,
            exe,
            engine: Engine::new(&exe_dir, preview),
            preview: preview.is_some(),
            config: RefCell::new(config),
            hotkeys: Cell::new(ProbeResponse::default()),
            status_ok: Cell::new(true),
            taskbar_created: Cell::new(0),
            started: Cell::new(false),
            modal_depth: Cell::new(0),
            pending_capture: Cell::new(None),
        }
    }

    /// Records the window handle (called while the window is being created).
    pub fn attach(&self, window: HWND) {
        self.window.set(window);
    }

    /// Forgets the window handle (called on `WM_NCDESTROY`, when it becomes invalid).
    pub fn detach(&self) {
        self.window.set(ptr::null_mut());
    }

    /// The app window.
    pub fn window(&self) -> HWND {
        self.window.get()
    }

    /// The module instance.
    pub fn instance(&self) -> HINSTANCE {
        self.instance
    }

    /// The DPI the window is currently laid out for.
    pub fn dpi(&self) -> Dpi {
        self.dpi.get()
    }

    fn child(&self, id: i32) -> Control {
        Control::child(self.window(), id)
    }

    fn show(&self, command: SHOW_WINDOW_CMD) {
        // SAFETY: no pointer arguments.
        unsafe { ShowWindow(self.window(), command) };
    }

    /// Builds the window contents, registers the hotkeys, writes the (normalised)
    /// settings back, adds the tray icon and performs the start-up `action`.
    ///
    /// Requests from other processes that arrive meanwhile are answered with 0 (see
    /// [`ipc::needs_startup`]); they are accepted once this returns.
    pub fn start(&self, action: Action) -> Result<(), UiError> {
        self.create_controls()?;

        let config = self.config.borrow().clone();
        let registered = self.register_hotkeys(&config);
        let autostart = self.write_autostart(config.autostart);
        let saved = self.store.save(&config).is_ok() && !matches!(autostart, Some(Err(_)));
        if let Err(error) = config.hotkeys() {
            self.set_status(config_error_message(error), false);
        } else if !registered {
            self.set_status(status::HOTKEY_TAKEN, false);
        } else if !saved {
            self.set_status(status::STARTUP_SAVE_FAILED, false);
        } else if autostart.is_none() {
            self.set_status(status::EXE_UNKNOWN, false);
        }

        // SAFETY: the literal is NUL-terminated.
        self.taskbar_created.set(unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) });
        self.add_tray_icon();
        self.started.set(true);

        let settings_requested =
            matches!(action, Action::ShowSettings | Action::ShowRecordingSettings);
        if settings_requested || !registered {
            self.show_settings(action == Action::ShowRecordingSettings);
        } else if action == Action::TakeScreenshot {
            self.capture(CaptureMode::Screenshot);
        } else if action == Action::ToggleRecording {
            self.capture(CaptureMode::Recording);
        }
        Ok(())
    }

    /// Adds the tray icon; while the taskbar is not ready (e.g. early during logon),
    /// a timer retries every few seconds.
    fn add_tray_icon(&self) {
        // Read once: if the window is destroyed during the shell call, `self.window()`
        // is detached (null), but the icon is registered under this handle.
        let window = self.window();
        let icon = self.tray_icon.borrow().handle();
        let state = tray::advance(window, icon, self.tray_state.get());
        // Shell_NotifyIconW dispatches sent messages; if one of them destroyed the
        // window meanwhile, an icon added afterwards would be orphaned.
        // SAFETY: no pointer arguments.
        if unsafe { IsWindow(window) } == 0 {
            tray::remove(window);
            return;
        }
        self.tray_state.set(state);
        let retry = state != TrayState::Ready && self.tray_retries.get() > 0;
        if retry {
            self.tray_retries.set(self.tray_retries.get() - 1);
        }
        // SAFETY: no pointer arguments; the timer belongs to our window.
        unsafe {
            if retry {
                SetTimer(window, TRAY_RETRY_TIMER, TRAY_RETRY_MS, None);
            } else {
                KillTimer(window, TRAY_RETRY_TIMER);
            }
        }
    }

    /// Loads the caption and taskbar icons for the window's DPI and hands them over.
    /// The tray icon is not affected: the taskbar draws at the system DPI.
    pub fn refresh_icons(&self) {
        let icons = WindowIcons::load(self.instance, self.dpi());
        icons.apply(self.window());
        // The previous icons are dropped only after the window has the new ones.
        drop(self.window_icons.replace(Some(icons)));
    }

    /// Runs a modal loop (folder picker, tray menu). Meanwhile captures are queued and
    /// quitting is refused (see [`while_modal`]); a queued capture starts afterwards.
    fn run_modal<T>(&self, modal: impl FnOnce() -> T) -> T {
        let result = self.modal_section(modal);
        if self.modal_depth.get() == 0 {
            if let Some(mode) = self.pending_capture.take() {
                self.capture(mode);
            }
        }
        result
    }

    /// Runs `section` as modal (captures are queued, quitting is refused) without
    /// starting a queued capture afterwards; the caller decides.
    fn modal_section<T>(&self, section: impl FnOnce() -> T) -> T {
        self.modal_depth.set(self.modal_depth.get() + 1);
        let result = section();
        self.modal_depth.set(self.modal_depth.get() - 1);
        result
    }

    fn in_modal(&self) -> bool {
        self.modal_depth.get() > 0
    }

    /// Lays the window out for `dpi`: new fonts, scaled and repositioned controls and,
    /// if given, the new window rectangle (as suggested by `WM_DPICHANGED`).
    pub fn apply_dpi(&self, dpi: Dpi, frame: Option<RECT>) {
        self.dpi.set(dpi);
        let scaled = Scaled::new(dpi);
        let children = self.children.borrow().clone();
        for (control, spec) in children {
            control.move_to(dpi.scale_rect(spec.rect));
            control.set_font(scaled.font(spec.font_role()));
        }
        // The old fonts are deleted only now that no control uses them any more.
        *self.scaled.borrow_mut() = scaled;
        if let Some(frame) = frame {
            // SAFETY: no pointer arguments.
            unsafe {
                SetWindowPos(
                    self.window(),
                    ptr::null_mut(),
                    frame.left,
                    frame.top,
                    frame.right - frame.left,
                    frame.bottom - frame.top,
                    REPOSITION,
                );
            }
        }
        self.refresh_icons();
        // SAFETY: a null rectangle means the whole window.
        unsafe { InvalidateRect(self.window(), ptr::null(), 1) };
    }

    /// Handles a window message; `None` lets `DefWindowProcW` handle it.
    pub fn handle_message(&self, message: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
        if (ipc::needs_startup(message) || message == WM_HOTKEY) && !self.started.get() {
            return Some(0);
        }
        let taskbar_created = self.taskbar_created.get();
        if taskbar_created != 0 && message == taskbar_created {
            // Explorer restarted: the icon has to be added again.
            self.tray_retries.set(TRAY_RETRIES);
            self.tray_state.set(TrayState::Missing);
            self.add_tray_icon();
            return Some(0);
        }
        let result = match message {
            WM_CLOSE => {
                self.show(SW_HIDE);
                0
            }
            WM_ENDSESSION if wparam != 0 => {
                self.unregister_hotkeys();
                tray::remove(self.window());
                0
            }
            WM_DESTROY => {
                self.unregister_hotkeys();
                tray::remove(self.window());
                // SAFETY: no arguments besides the exit code.
                unsafe { PostQuitMessage(0) };
                0
            }
            // WM_PAINT paints the whole background; erasing first would only flicker.
            WM_ERASEBKGND => 1,
            WM_PAINT => {
                self.paint_background();
                0
            }
            WM_TIMER if wparam == TRAY_RETRY_TIMER => {
                self.add_tray_icon();
                0
            }
            WM_DPICHANGED => {
                // SAFETY: for WM_DPICHANGED, lParam is null or points to the suggested
                // window RECT, valid for the duration of the message.
                let frame = unsafe { (lparam as *const RECT).as_ref() }.copied();
                self.apply_dpi(Dpi::new(low_word(wparam)), frame);
                0
            }
            WM_CTLCOLORSTATIC | WM_CTLCOLORBTN => {
                let is_status = lparam as HWND == self.child(id::STATUS).handle();
                let color =
                    if is_status && !self.status_ok.get() { theme::ERROR } else { theme::INK };
                color_control(wparam as HDC, color, theme::BACKGROUND);
                self.brushes.background() as LRESULT
            }
            WM_CTLCOLOREDIT => {
                color_control(wparam as HDC, theme::INK, theme::FIELD);
                self.brushes.field() as LRESULT
            }
            WM_DRAWITEM => {
                // SAFETY: for WM_DRAWITEM, lParam is null or points to a DRAWITEMSTRUCT
                // that is valid for the duration of the message.
                let item = unsafe { (lparam as *const DRAWITEMSTRUCT).as_ref() }?;
                // The borrow keeps the brushes, pens and font alive for the whole paint;
                // nothing in it can re-enter this window (see the module docs).
                let scaled = self.scaled.borrow();
                theme::draw_button(item, scaled.button_paint(item), self.dpi());
                1
            }
            WM_COMMAND => {
                if let Some(control) = clicked_command(wparam, lparam) {
                    self.command(control);
                }
                0
            }
            _ => return self.handle_request(message, wparam, lparam),
        };
        Some(result)
    }

    /// Handles hotkeys and requests from other processes and the tray; `None` for
    /// anything else.
    fn handle_request(&self, message: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
        let result = match message {
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
                if self.quit() {
                    ipc::QUIT_ACCEPTED
                } else {
                    ipc::QUIT_REFUSED
                }
            }
            ipc::PROBE => self.hotkeys.get().to_lresult(),
            ipc::AUDIO_SETTINGS => {
                self.apply_audio_settings(AudioSettings::from_wparam(wparam));
                1
            }
            ipc::TRAY_CALLBACK => {
                if let Some(event) = TrayEvent::decode(wparam, lparam) {
                    self.tray_event(event);
                }
                0
            }
            _ => return None,
        };
        Some(result)
    }

    fn create_controls(&self) -> Result<(), UiError> {
        let dpi = self.dpi();
        for spec in CONTROLS {
            let (class, ex_style, style): (_, WINDOW_EX_STYLE, WINDOW_STYLE) = match spec.kind {
                ControlKind::Label(_) => (w!("STATIC"), 0, 0),
                ControlKind::Edit => {
                    (w!("EDIT"), WS_EX_CLIENTEDGE, WS_TABSTOP | ES_AUTOHSCROLL.cast_unsigned())
                }
                ControlKind::Button => (w!("BUTTON"), 0, WS_TABSTOP | BS_OWNERDRAW.cast_unsigned()),
                ControlKind::Checkbox => {
                    (w!("BUTTON"), 0, WS_TABSTOP | BS_AUTOCHECKBOX.cast_unsigned())
                }
                ControlKind::Combo(_) => {
                    (w!("COMBOBOX"), 0, WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST.cast_unsigned())
                }
            };
            let creation = Creation {
                parent: self.window(),
                instance: self.instance,
                class,
                text: spec.text,
                ex_style,
                style,
                rect: dpi.scale_rect(spec.rect),
                id: spec.id,
            };
            let control = Control::create(&creation)
                .map_err(|code| UiError { kind: UiErrorKind::CreateControls, code })?;
            let font = self.scaled.borrow().font(spec.font_role());
            control.set_font(font);
            match spec.kind {
                ControlKind::Edit => control.limit_text(EDIT_LIMIT),
                ControlKind::Checkbox => {
                    // Without visual styles the check box honours WM_CTLCOLORSTATIC,
                    // so its label is readable on the dark background.
                    // SAFETY: both literals are NUL-terminated.
                    unsafe { SetWindowTheme(control.handle(), w!(""), w!("")) };
                }
                ControlKind::Combo(entries) => {
                    for entry in entries {
                        control.add_entry(entry);
                    }
                }
                ControlKind::Label(_) | ControlKind::Button => {}
            }
            self.children.borrow_mut().push((control, spec));
        }
        self.populate();
        Ok(())
    }

    /// Shows the saved settings in the controls (discarding unsaved edits).
    fn populate(&self) {
        let config = self.config.borrow().clone();
        self.child(id::SCREENSHOT_HOTKEY).set_text(&config.screenshot_hotkey);
        self.child(id::SCREENSHOT_FOLDER).set_text(&config.screenshot_folder);
        self.child(id::RECORDING_HOTKEY).set_text(&config.recording_hotkey);
        self.child(id::RECORDING_FOLDER).set_text(&config.recording_folder);
        self.show_audio_settings(&config);
        self.child(id::AUTOSTART).set_checked(config.autostart);
        self.child(id::FRAME_RATE).select(config.frame_rate.index());
        self.child(id::COUNTDOWN).select(config.countdown.index());
    }

    fn show_audio_settings(&self, config: &Config) {
        self.child(id::MICROPHONE).set_checked(config.microphone);
        self.child(id::SYSTEM_AUDIO).set_checked(config.system_audio);
        self.child(id::SHOW_CLICKS).set_checked(config.show_clicks);
    }

    /// The settings as currently entered in the window.
    fn read_controls(&self) -> Config {
        let text = |id| self.child(id).text();
        let checked = |id| self.child(id).is_checked();
        Config {
            screenshot_hotkey: text(id::SCREENSHOT_HOTKEY),
            recording_hotkey: text(id::RECORDING_HOTKEY),
            screenshot_folder: PathBuf::from(self.child(id::SCREENSHOT_FOLDER).text_os()),
            recording_folder: PathBuf::from(self.child(id::RECORDING_FOLDER).text_os()),
            microphone: checked(id::MICROPHONE),
            system_audio: checked(id::SYSTEM_AUDIO),
            show_clicks: checked(id::SHOW_CLICKS),
            autostart: checked(id::AUTOSTART),
            frame_rate: FrameRate::from_index(self.child(id::FRAME_RATE).selection()),
            countdown: Countdown::from_index(self.child(id::COUNTDOWN).selection()),
        }
    }

    /// Shows the window. Only a hidden window is refilled from the saved settings, so
    /// unsaved edits survive (e.g. a second "settings" request or an open picker).
    fn show_settings(&self, focus_recording: bool) {
        // SAFETY: no pointer arguments.
        if unsafe { IsWindowVisible(self.window()) } == 0 {
            self.populate();
        }
        self.show(SW_RESTORE);
        let focus = if focus_recording { id::RECORDING_HOTKEY } else { id::SCREENSHOT_HOTKEY };
        // SAFETY: no pointer arguments.
        unsafe {
            SetForegroundWindow(self.window());
            SetFocus(self.child(focus).handle());
        }
    }

    fn set_status(&self, text: &str, ok: bool) {
        self.status_ok.set(ok);
        let status = self.child(id::STATUS);
        status.set_text(text);
        // SAFETY: a null rectangle means the whole control.
        unsafe { InvalidateRect(status.handle(), ptr::null(), 1) };
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

    /// Releases both hotkeys; `--probe` reports them as missing from now on (also
    /// while the window is being torn down).
    fn unregister_hotkeys(&self) {
        // SAFETY: no pointer arguments; unregistering an unknown id fails harmlessly.
        unsafe {
            UnregisterHotKey(self.window(), HOTKEY_SCREENSHOT);
            UnregisterHotKey(self.window(), HOTKEY_RECORDING);
        }
        self.hotkeys.set(ProbeResponse::default());
    }

    /// Re-registers the hotkeys of the saved settings after a failed change.
    fn restore_hotkeys(&self, saved: &Config) {
        self.unregister_hotkeys();
        self.register_hotkeys(saved);
    }

    /// Writes (or removes) the autostart entry. Preview mode never touches it and
    /// reports success; `None` means nothing was written because the path of this
    /// executable is unknown (an empty path must never end up in the Run key).
    fn write_autostart(&self, enabled: bool) -> Option<io::Result<()>> {
        if self.preview {
            return Some(Ok(()));
        }
        self.exe.as_deref().map(|exe| autostart::set_enabled(enabled, exe))
    }

    /// "Speichern" ("Save"): validate, re-register hotkeys, update autostart, write
    /// the file. Every failure rolls back to the previous settings.
    fn save(&self) {
        let saved = self.config.borrow().clone();
        let draft = self.read_controls();
        if let Err(error) = draft.validate() {
            self.set_status(config_error_message(error), false);
            return;
        }

        self.unregister_hotkeys();
        if !self.register_hotkeys(&draft) {
            self.restore_hotkeys(&saved);
            self.set_status(status::HOTKEY_TAKEN_KEEPING_OLD, false);
            return;
        }

        let autostart = self.write_autostart(draft.autostart);
        let stored = !matches!(autostart, Some(Err(_))) && self.store.save(&draft).is_ok();
        if !stored {
            self.restore_hotkeys(&saved);
            // Best effort: the user already sees the error below.
            let _ = self.write_autostart(saved.autostart);
            self.set_status(status::SAVE_FAILED, false);
            return;
        }

        *self.config.borrow_mut() = draft;
        if autostart.is_some() {
            self.set_status(status::SAVED, true);
        } else {
            self.set_status(status::EXE_UNKNOWN, false);
        }
    }

    fn command(&self, control: i32) {
        match control {
            // Enter in the dialog: same as "Speichern" ("Save").
            IDOK | id::SAVE => self.save(),
            // Escape (sent by IsDialogMessage unless a drop-down list is open).
            IDCANCEL => self.show(SW_HIDE),
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
        let initial = PathBuf::from(edit.text_os());
        if let Some(folder) = self.run_modal(|| folder_picker::pick_folder(self.window(), &initial))
        {
            edit.set_text(&folder);
        }
    }

    /// Opens the saved output folder in Explorer, if it exists.
    fn open_folder(&self, mode: CaptureMode) {
        let folder = {
            let config = self.config.borrow();
            PathBuf::from(match mode {
                CaptureMode::Screenshot => &config.screenshot_folder,
                CaptureMode::Recording => &config.recording_folder,
            })
        };
        if !folder.is_dir() {
            self.set_status(status::FOLDER_MISSING, false);
            return;
        }
        // Explorer reports its own errors; nothing useful to add here.
        let _ = explorer_command(&folder).spawn();
    }

    /// Hides the window and hands the capture to the engine. While a modal loop runs,
    /// the request is queued instead: hiding the window would orphan the dialog.
    fn capture(&self, mode: CaptureMode) {
        if while_modal(Request::Capture, self.in_modal()) == Handling::Defer {
            self.pending_capture.set(Some(mode));
            return;
        }
        self.show(SW_HIDE);
        if self.engine.start_capture(mode).failed() {
            self.show_settings(mode == CaptureMode::Recording);
            self.set_status(status::ENGINE_FAILED, false);
        }
    }

    /// Quits app and engine. Refused (returns `false`, with a status message) while
    /// a dialog of this app is open or the engine is in the middle of a capture.
    fn quit(&self) -> bool {
        if while_modal(Request::Quit, self.in_modal()) == Handling::Refuse {
            self.set_status(status::DIALOG_OPEN, false);
            return false;
        }
        // The state query waits for the engine and dispatches incoming sent messages
        // meanwhile; as a modal section, a nested quit is refused and captures wait.
        let busy = self.modal_section(|| self.engine.is_busy());
        let queued = self.pending_capture.take();
        match quit_decision(busy, queued.is_some()) {
            QuitDecision::Quit => {
                self.engine.request_quit();
                // SAFETY: destroys our own window; WM_DESTROY then ends the message loop.
                unsafe { DestroyWindow(self.window()) };
                true
            }
            QuitDecision::RefuseBusy => {
                self.show_settings(true);
                self.set_status(status::BUSY, false);
                false
            }
            QuitDecision::RefuseForCapture => {
                // The user asked for a capture while we checked: do it, keep running.
                if let Some(mode) = queued {
                    self.capture(mode);
                }
                false
            }
        }
    }

    /// Stores audio options changed in the engine's recording toolbar. The new values
    /// are only adopted if they could be saved; the check boxes show what is in effect.
    fn apply_audio_settings(&self, audio: AudioSettings) {
        let mut updated = self.config.borrow().clone();
        updated.microphone = audio.microphone;
        updated.system_audio = audio.system_audio;
        updated.show_clicks = audio.show_clicks;
        if self.store.save(&updated).is_ok() {
            *self.config.borrow_mut() = updated;
        } else {
            self.set_status(status::AUDIO_SAVE_FAILED, false);
        }
        let current = self.config.borrow().clone();
        self.show_audio_settings(&current);
    }

    fn tray_event(&self, event: TrayEvent) {
        match event {
            TrayEvent::Select => self.show_settings(false),
            TrayEvent::ContextMenu { x, y } => {
                match self.run_modal(|| tray::show_menu(self.window(), x, y)) {
                    Some(MenuCommand::Screenshot) => self.capture(CaptureMode::Screenshot),
                    Some(MenuCommand::Recording) => self.capture(CaptureMode::Recording),
                    Some(MenuCommand::Settings) => self.show_settings(false),
                    Some(MenuCommand::Quit) => {
                        // A refusal is shown in the window's status line.
                        let _ = self.quit();
                    }
                    None => {}
                }
            }
        }
    }

    fn paint_background(&self) {
        // SAFETY: PAINTSTRUCT is a plain C struct for which all-zero is valid.
        let mut paint: PAINTSTRUCT = unsafe { std::mem::zeroed() };
        // SAFETY: called while handling WM_PAINT for our window; `paint` outlives the
        // BeginPaint/EndPaint pair.
        unsafe {
            let dc = BeginPaint(self.window(), &raw mut paint);
            FillRect(dc, &raw const paint.rcPaint, self.brushes.background());
            EndPaint(self.window(), &raw const paint);
        }
    }
}

/// A request that may arrive while a modal loop runs.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Request {
    /// Hide the window and capture.
    Capture,
    /// Quit the app.
    Quit,
}

/// What to do with a [`Request`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Handling {
    /// Handle it right away.
    Now,
    /// Remember it and handle it when the modal loop ends.
    Defer,
    /// Do not handle it.
    Refuse,
}

/// The policy while a modal loop (folder picker, tray menu) is running: hiding the
/// window or destroying it would pull the dialog's owner away, so a capture waits
/// until the dialog is closed and quitting is refused.
const fn while_modal(request: Request, modal: bool) -> Handling {
    match (request, modal) {
        (_, false) => Handling::Now,
        (Request::Capture, true) => Handling::Defer,
        (Request::Quit, true) => Handling::Refuse,
    }
}

/// The outcome of a quit request after the engine was asked whether it is busy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum QuitDecision {
    /// Quit app and engine.
    Quit,
    /// The engine is recording or selecting a region.
    RefuseBusy,
    /// A capture was requested while the engine was being asked; it starts instead.
    RefuseForCapture,
}

const fn quit_decision(engine_busy: bool, capture_queued: bool) -> QuitDecision {
    match (engine_busy, capture_queued) {
        (true, _) => QuitDecision::RefuseBusy,
        (false, true) => QuitDecision::RefuseForCapture,
        (false, false) => QuitDecision::Quit,
    }
}

/// The command id of a `WM_COMMAND` that means "do it": a click (`BN_CLICKED`, which
/// is also what menus and the dialog manager's `IDOK`/`IDCANCEL` send) or an
/// accelerator (notification 1 without a control). Other notifications, such as
/// `BN_DOUBLECLICKED` from owner-drawn buttons or `EN_CHANGE`, are not commands.
fn clicked_command(wparam: WPARAM, lparam: LPARAM) -> Option<i32> {
    const ACCELERATOR: u32 = 1;
    let notification = u32::try_from((wparam >> 16) & 0xFFFF).ok()?;
    let is_command = notification == BN_CLICKED || (notification == ACCELERATOR && lparam == 0);
    is_command.then(|| low_word(wparam).cast_signed())
}

/// The status-line text explaining why settings cannot be applied.
fn config_error_message(error: ConfigError) -> &'static str {
    match error {
        ConfigError::InvalidHotkey(ParseHotkeyError::ShiftOnly) => status::SHIFT_ONLY_HOTKEY,
        ConfigError::InvalidHotkey(_) => status::INVALID_HOTKEY,
        ConfigError::DuplicateHotkey => status::DUPLICATE_HOTKEY,
        ConfigError::InvalidFolder => status::INVALID_FOLDER,
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

/// `LOWORD(wParam)`, e.g. the control id of a `WM_COMMAND` or the DPI of a
/// `WM_DPICHANGED`.
fn low_word(value: WPARAM) -> u32 {
    u32::try_from(value & 0xFFFF).unwrap_or_default()
}

/// `explorer.exe "<folder>"` from the Windows directory.
fn explorer_command(folder: &Path) -> Command {
    let explorer = env::var_os("SystemRoot").map_or_else(
        || PathBuf::from("explorer.exe"),
        |root| Path::new(&root).join("explorer.exe"),
    );
    let mut command = Command::new(explorer);
    command.raw_arg(explorer_argument(folder));
    command
}

/// Explorer parses its command line itself (a backslash before a quote is not an
/// escape), so the folder is quoted verbatim instead of with `std`'s MSVC rules.
fn explorer_argument(folder: &Path) -> OsString {
    let mut argument = OsString::from("\"");
    argument.push(folder);
    argument.push("\"");
    argument
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn config_errors_have_status_messages() {
        let shift_only = ConfigError::InvalidHotkey(ParseHotkeyError::ShiftOnly);
        assert_eq!(config_error_message(shift_only), status::SHIFT_ONLY_HOTKEY);
        let unknown = ConfigError::InvalidHotkey(ParseHotkeyError::UnknownKey);
        assert_eq!(config_error_message(unknown), status::INVALID_HOTKEY);
        assert_eq!(config_error_message(ConfigError::DuplicateHotkey), status::DUPLICATE_HOTKEY);
        assert_eq!(config_error_message(ConfigError::InvalidFolder), status::INVALID_FOLDER);
    }

    #[test]
    fn only_clicks_and_accelerators_are_commands() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{BN_DOUBLECLICKED, EN_CHANGE};
        let button: LPARAM = 0x1234; // any control handle
        let packed = |notification: u32, id: i32| {
            (usize::try_from(notification).unwrap() << 16) | usize::try_from(id).unwrap()
        };
        assert_eq!(clicked_command(packed(BN_CLICKED, id::SAVE), button), Some(id::SAVE));
        assert_eq!(clicked_command(packed(BN_CLICKED, IDOK), 0), Some(IDOK), "Enter");
        assert_eq!(clicked_command(packed(BN_CLICKED, IDCANCEL), 0), Some(IDCANCEL), "Escape");
        assert_eq!(clicked_command(packed(1, id::SAVE), 0), Some(id::SAVE), "accelerator");
        assert_eq!(clicked_command(packed(BN_DOUBLECLICKED, id::SAVE), button), None);
        assert_eq!(clicked_command(packed(1, id::SAVE), button), None, "BN_PAINT, not a key");
        let edit_change = packed(EN_CHANGE, id::SCREENSHOT_HOTKEY);
        assert_eq!(clicked_command(edit_change, button), None);
    }

    #[test]
    fn quitting_never_races_a_capture() {
        assert_eq!(quit_decision(false, false), QuitDecision::Quit);
        assert_eq!(quit_decision(true, false), QuitDecision::RefuseBusy);
        assert_eq!(quit_decision(true, true), QuitDecision::RefuseBusy);
        assert_eq!(quit_decision(false, true), QuitDecision::RefuseForCapture);
    }

    #[test]
    fn modal_loops_defer_captures_and_refuse_quitting() {
        assert_eq!(while_modal(Request::Capture, false), Handling::Now);
        assert_eq!(while_modal(Request::Quit, false), Handling::Now);
        assert_eq!(while_modal(Request::Capture, true), Handling::Defer);
        assert_eq!(while_modal(Request::Quit, true), Handling::Refuse);
    }

    #[test]
    fn low_word_extracts_the_low_16_bits() {
        assert_eq!(low_word(0x0001_0090), 0x90);
        assert_eq!(low_word(0x00C0_00C0), 192);
        assert_eq!(low_word(usize::MAX), 0xFFFF);
    }

    #[test]
    fn explorer_gets_the_folder_quoted_verbatim() {
        assert_eq!(
            explorer_argument(Path::new(r"C:\Users\Ada\Bilder")),
            r#""C:\Users\Ada\Bilder""#
        );
        assert_eq!(explorer_argument(Path::new(r"D:\")), r#""D:\""#);
        let command = explorer_command(Path::new(r"C:\Videos"));
        assert!(Path::new(command.get_program()).ends_with("explorer.exe"));
    }
}
