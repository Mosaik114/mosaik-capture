//! The primary instance: settings window, tray icon and message loop.

mod app;
mod controls;
mod folder_picker;
mod icons;
mod layout;
mod strings;
mod theme;
mod tray;

use std::{env, mem, ptr};

use windows_sys::Win32::Foundation::{
    FALSE, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, TRUE, WPARAM,
};
use windows_sys::Win32::Graphics::Dwm::{DWMWA_USE_IMMERSIVE_DARK_MODE, DwmSetWindowAttribute};
use windows_sys::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint,
};
use windows_sys::Win32::System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::HiDpi::{
    AdjustWindowRectExForDpi, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForMonitor,
    GetDpiForSystem, GetDpiForWindow, MDT_EFFECTIVE_DPI, SetProcessDpiAwarenessContext,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow,
    DispatchMessageW, GWLP_USERDATA, GetCursorPos, GetMessageW, GetWindowLongPtrW, IDC_ARROW,
    IsDialogMessageW, IsWindow, LoadCursorW, MSG, RegisterClassW, SPI_GETWORKAREA,
    SetWindowLongPtrW, SystemParametersInfoW, TranslateMessage, UnregisterClassW, WINDOW_EX_STYLE,
    WINDOW_STYLE, WM_NCCREATE, WM_NCDESTROY, WNDCLASSW, WS_CAPTION, WS_EX_APPWINDOW,
    WS_MINIMIZEBOX, WS_SYSMENU,
};

use windows_sys::core::PCWSTR;

use crate::cli::Invocation;
use crate::ipc;
use crate::store::SettingsStore;
use crate::wide::to_wide;
use app::App;
use layout::Dpi;

/// Style of the settings window: caption with close and minimise buttons.
const WINDOW_STYLE: WINDOW_STYLE = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
/// Extended style of the settings window: always shown on the taskbar.
const WINDOW_EX_STYLE: WINDOW_EX_STYLE = WS_EX_APPWINDOW;

/// Why the user interface could not be run.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UiError {
    /// `RegisterClassW` failed.
    RegisterClass,
    /// `CreateWindowExW` failed for the settings window.
    CreateWindow,
    /// `CreateWindowExW` failed for one of the window's controls.
    CreateControls,
    /// `GetMessageW` reported an error.
    MessageLoop,
}

/// Runs the primary instance until the user quits.
pub fn run(invocation: &Invocation) -> Result<(), UiError> {
    // SAFETY: a process-wide setting without pointer arguments, set before any window.
    // The manifest requests the same; this covers hosts that ignore it.
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let _com = ComApartment::enter();

    let preview = invocation.preview.as_deref();
    let store = match preview {
        Some(path) => SettingsStore::at(path.to_path_buf()),
        None => SettingsStore::user_default(),
    };
    let mut config = store.load();
    if preview.is_some() {
        config.autostart = false;
    }

    // SAFETY: a null name returns the handle of the executable itself.
    let instance = unsafe { GetModuleHandleW(ptr::null()) };
    // The window opens on the monitor the user is working on.
    let monitor = Monitor::under_cursor();
    let exe = env::current_exe().unwrap_or_default();
    // Boxed: the window procedure keeps the address, so it must never move.
    let app = Box::new(App::new(instance, monitor.dpi, store, config, exe, preview));

    // Declared after `app`, so the class is unregistered before the App (and with it
    // the class background brush) is dropped.
    let class = register_class(&app, ipc::WINDOW_CLASS)?;
    let window = create_window(&app, &class, &monitor)?;

    let result = app.start(invocation.action).and_then(|()| run_message_loop(window));
    // Normally the window is gone already. If not (an error), destroy it while `app`
    // is alive; WM_NCDESTROY then detaches `app` from the window.
    // SAFETY: no pointer arguments; `window` is our window or already invalid.
    if unsafe { IsWindow(window) } != 0 {
        // SAFETY: see above.
        unsafe { DestroyWindow(window) };
    }
    result
}

/// COM single-threaded apartment for the current thread (needed by the folder
/// picker), left again on drop if entering it succeeded.
struct ComApartment {
    entered: bool,
}

impl ComApartment {
    fn enter() -> Self {
        // SAFETY: the reserved pointer must be null.
        let result =
            unsafe { CoInitializeEx(ptr::null(), COINIT_APARTMENTTHREADED.cast_unsigned()) };
        // S_OK and S_FALSE (already initialised) both need a matching CoUninitialize;
        // failures such as RPC_E_CHANGED_MODE must not be balanced.
        Self { entered: result >= 0 }
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        if self.entered {
            // SAFETY: balances the successful CoInitializeEx on the same thread.
            unsafe { CoUninitialize() };
        }
    }
}

/// A registered window class, unregistered on drop.
#[derive(Debug)]
struct WindowClass {
    name: Vec<u16>,
    instance: HINSTANCE,
}

impl WindowClass {
    fn name(&self) -> PCWSTR {
        self.name.as_ptr()
    }
}

impl Drop for WindowClass {
    fn drop(&mut self) {
        // SAFETY: the name is NUL-terminated; fails harmlessly if a window of the class
        // still exists.
        unsafe { UnregisterClassW(self.name.as_ptr(), self.instance) };
    }
}

/// Registers the window class. The returned guard must be dropped before `app`,
/// whose background brush the class refers to.
fn register_class(app: &App, name: &str) -> Result<WindowClass, UiError> {
    let class_name = to_wide(name);
    let class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        cbClsExtra: 0,
        cbWndExtra: 0,
        hInstance: app.instance(),
        hIcon: icons::class_icon(app.instance()),
        // SAFETY: loads a predefined system cursor.
        hCursor: unsafe { LoadCursorW(ptr::null_mut(), IDC_ARROW) },
        hbrBackground: app.brushes().background(),
        lpszMenuName: ptr::null(),
        lpszClassName: class_name.as_ptr(),
    };
    // SAFETY: `class` and the class name it points to outlive the call.
    if unsafe { RegisterClassW(&raw const class) } == 0 {
        return Err(UiError::RegisterClass);
    }
    Ok(WindowClass { name: class_name, instance: app.instance() })
}

/// Outer window size for the settings window's client area at `dpi`.
fn frame_size(dpi: Dpi) -> (i32, i32) {
    let (width, height) = dpi.client_size();
    let mut frame = RECT { left: 0, top: 0, right: width, bottom: height };
    // SAFETY: `frame` outlives the call.
    unsafe {
        AdjustWindowRectExForDpi(&raw mut frame, WINDOW_STYLE, FALSE, WINDOW_EX_STYLE, dpi.value());
    }
    (frame.right - frame.left, frame.bottom - frame.top)
}

/// The window rectangle that centres a window of `dpi`'s size in the work area.
/// The monitor the window should open on: work area and DPI.
#[derive(Clone, Copy, Debug)]
struct Monitor {
    /// `[left, top, right, bottom]` of the work area (without the taskbar).
    work_area: [i32; 4],
    dpi: Dpi,
}

impl Monitor {
    /// The monitor under the mouse cursor (or the nearest one).
    fn under_cursor() -> Self {
        let mut cursor = POINT { x: 0, y: 0 };
        // SAFETY: `cursor` outlives the call; on failure it stays (0, 0), i.e. the
        // primary monitor.
        unsafe { GetCursorPos(&raw mut cursor) };
        // SAFETY: no pointer arguments.
        let monitor = unsafe { MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) };
        // SAFETY: MONITORINFO is plain data; `cbSize` is set as the API requires.
        let mut info: MONITORINFO = unsafe { mem::zeroed() };
        info.cbSize = u32::try_from(mem::size_of::<MONITORINFO>()).unwrap_or(u32::MAX);
        // SAFETY: `info` outlives the call.
        let has_info = unsafe { GetMonitorInfoW(monitor, &raw mut info) } != 0;
        let (mut dpi_x, mut dpi_y) = (0u32, 0u32);
        // SAFETY: both outputs outlive the call.
        let has_dpi =
            unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &raw mut dpi_x, &raw mut dpi_y) }
                >= 0;
        Self {
            work_area: rect_edges(if has_info { info.rcWork } else { primary_work_area() }),
            // SAFETY: no arguments.
            dpi: Dpi::new(if has_dpi { dpi_x } else { unsafe { GetDpiForSystem() } }),
        }
    }

    /// The window rectangle that centres a window of `dpi`'s size in the work area.
    fn centered_frame(&self, dpi: Dpi) -> RECT {
        let (width, height) = frame_size(dpi);
        let (x, y) = layout::centered(self.work_area, width, height);
        RECT { left: x, top: y, right: x + width, bottom: y + height }
    }
}

const fn rect_edges(rect: RECT) -> [i32; 4] {
    [rect.left, rect.top, rect.right, rect.bottom]
}

/// The primary monitor's work area, the fallback if monitor information is missing.
fn primary_work_area() -> RECT {
    let mut area = RECT { left: 0, top: 0, right: 0, bottom: 0 };
    // SAFETY: SPI_GETWORKAREA writes one RECT into `area`, which outlives the call.
    unsafe { SystemParametersInfoW(SPI_GETWORKAREA, 0, (&raw mut area).cast(), 0) };
    area
}

/// Creates the (hidden) settings window, sized for the DPI of the monitor it lands on.
fn create_window(app: &App, class: &WindowClass, monitor: &Monitor) -> Result<HWND, UiError> {
    let frame = monitor.centered_frame(app.dpi());
    let title = to_wide(strings::WINDOW_TITLE);
    // SAFETY: class name and title are NUL-terminated and outlive the call. `app`
    // outlives the window (see `run`), so handing its address to WM_NCCREATE is sound.
    let window = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE,
            class.name(),
            title.as_ptr(),
            WINDOW_STYLE,
            frame.left,
            frame.top,
            frame.right - frame.left,
            frame.bottom - frame.top,
            ptr::null_mut(),
            ptr::null_mut(),
            app.instance(),
            ptr::from_ref(app).cast(),
        )
    };
    if window.is_null() {
        return Err(UiError::CreateWindow);
    }

    let dark = TRUE;
    // SAFETY: `dark` is a BOOL that outlives the call; the size matches.
    unsafe {
        DwmSetWindowAttribute(
            window,
            DWMWA_USE_IMMERSIVE_DARK_MODE.cast_unsigned(),
            (&raw const dark).cast(),
            u32::try_from(mem::size_of_val(&dark)).unwrap_or(4),
        );
    }

    // The system DPI was only a guess: the work area's monitor may use another one.
    // SAFETY: no pointer arguments.
    let actual = Dpi::new(unsafe { GetDpiForWindow(window) });
    if actual == app.dpi() {
        app.refresh_icons();
    } else {
        app.apply_dpi(actual, Some(monitor.centered_frame(actual)));
    }
    Ok(window)
}

/// Pumps messages until `WM_QUIT`. `IsDialogMessageW` gives the window dialog-style
/// keyboard handling: Tab/arrow navigation, Enter (`IDOK`) and Escape (`IDCANCEL`).
fn run_message_loop(window: HWND) -> Result<(), UiError> {
    // SAFETY: MSG is a plain C struct for which all-zero is valid.
    let mut message: MSG = unsafe { mem::zeroed() };
    loop {
        // SAFETY: `message` outlives every call that reads or writes it.
        unsafe {
            match GetMessageW(&raw mut message, ptr::null_mut(), 0, 0) {
                0 => return Ok(()),
                -1 => return Err(UiError::MessageLoop),
                _ => {
                    if IsDialogMessageW(window, &raw const message) == 0 {
                        TranslateMessage(&raw const message);
                        DispatchMessageW(&raw const message);
                    }
                }
            }
        }
    }
}

/// Window procedure: finds the [`App`] stored in `GWLP_USERDATA` and delegates.
unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_NCCREATE {
        // SAFETY: for WM_NCCREATE, lParam is null or points to the window's
        // CREATESTRUCTW, valid for the duration of the message.
        let create = unsafe { (lparam as *const CREATESTRUCTW).as_ref() };
        let params = create.map_or(ptr::null_mut(), |create| create.lpCreateParams);
        // SAFETY: `create_window` passes the boxed `App`, which outlives the window;
        // a null parameter (someone else creating our class) is simply not attached.
        if let Some(app) = unsafe { params.cast::<App>().as_ref() } {
            // SAFETY: storing a pointer in our own window's user data is plain data.
            unsafe { SetWindowLongPtrW(window, GWLP_USERDATA, ptr::from_ref(app) as isize) };
            app.attach(window);
        }
    }

    // SAFETY: reads plain data from our own window.
    let app = unsafe { GetWindowLongPtrW(window, GWLP_USERDATA) } as *const App;
    // SAFETY: a non-null value was stored above and points to the boxed App, which
    // outlives the window; it is cleared on WM_NCDESTROY and after the loop.
    let result =
        unsafe { app.as_ref() }.and_then(|app| app.handle_message(message, wparam, lparam));
    if message == WM_NCDESTROY {
        // SAFETY: see above.
        unsafe { SetWindowLongPtrW(window, GWLP_USERDATA, 0) };
    }
    // SAFETY: default handling for everything the app does not handle itself.
    result.unwrap_or_else(|| unsafe { DefWindowProcW(window, message, wparam, lparam) })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cli::Action;
    use crate::config::{Config, DefaultFolders, ProfileSource};
    use crate::store::IniFile;
    use controls::Control;
    use layout::id;
    use std::ffi::OsStr;
    use std::path::{Path, PathBuf};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        BN_DOUBLECLICKED, GetClientRect, ICON_SMALL, IDCANCEL, IDOK, IsWindow, IsWindowVisible,
        SendMessageW, WM_CLOSE, WM_COMMAND, WM_DPICHANGED, WM_GETICON,
    };

    fn send(window: HWND, message: u32, wparam: WPARAM) -> LRESULT {
        // SAFETY: the messages used with this helper carry no pointers.
        unsafe { SendMessageW(window, message, wparam, 0) }
    }

    fn command(window: HWND, id: i32) {
        send(window, WM_COMMAND, usize::try_from(id).unwrap());
    }

    fn is_visible(window: HWND) -> bool {
        // SAFETY: no pointer arguments.
        unsafe { IsWindowVisible(window) != 0 }
    }

    fn client_width(window: HWND) -> i32 {
        let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
        // SAFETY: `rect` outlives the call.
        unsafe { GetClientRect(window, &raw mut rect) };
        rect.right - rect.left
    }

    fn status(window: HWND) -> String {
        Control::child(window, id::STATUS).text()
    }

    /// Enables the end-to-end test of the real window.
    const UI_TESTS_VAR: &str = "MOSAIK_UI_TESTS";

    /// Destroys the window (unregistering the hotkeys) and removes the temp folder,
    /// even if an assertion fails.
    struct Cleanup {
        window: HWND,
        dir: PathBuf,
    }

    impl Drop for Cleanup {
        fn drop(&mut self) {
            // SAFETY: destroys the test window while its `App` is still alive.
            unsafe { DestroyWindow(self.window) };
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    /// Drives the real settings window: requests during start-up, probe, validation,
    /// saving, keyboard commands, unsaved edits, a DPI change, icons and the engine's
    /// audio message.
    ///
    /// It only touches this process (a private window class, a temp settings file in
    /// preview mode, no engine, no registry), but it creates a window and registers
    /// two unusual global hotkeys, so it is opt-in: it runs only when the environment
    /// variable `MOSAIK_UI_TESTS` is `1`. CI sets it for `cargo test`; locally run
    /// `MOSAIK_UI_TESTS=1 cargo test settings_window` (on a spare desktop if you like).
    #[test]
    fn settings_window_end_to_end() {
        if env::var_os(UI_TESTS_VAR).is_none_or(|value| value != "1") {
            eprintln!("skipped: set {UI_TESTS_VAR}=1 to drive the real settings window");
            return;
        }
        let dir = env::temp_dir().join(format!("mosaik-capture-ui-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let ini = dir.join("settings.ini");
        let folders = DefaultFolders::new(Path::new(r"C:\Bilder"), Path::new(r"C:\Videos"));
        let mut config = Config::defaults(&folders);
        config.screenshot_hotkey = "Strg+Alt+Shift+F23".to_owned();
        config.recording_hotkey = "Strg+Alt+Shift+F24".to_owned();
        config.autostart = false;

        // SAFETY: a null name returns the handle of the executable itself.
        let instance = unsafe { GetModuleHandleW(ptr::null()) };
        let app = Box::new(App::new(
            instance,
            Dpi::new(96),
            SettingsStore::at(ini.clone()),
            config.clone(),
            env::current_exe().unwrap(),
            Some(&ini),
        ));
        let class = register_class(&app, "MosaikCapture.Host.Test").unwrap();
        let window = create_window(&app, &class, &Monitor::under_cursor()).unwrap();
        let _cleanup = Cleanup { window, dir: dir.clone() };
        // Whatever monitor the window landed on, lay it out for 96 DPI first.
        app.apply_dpi(Dpi::new(96), None);

        // Requests from outside are not handled before start-up has finished.
        assert_eq!(send(window, ipc::PROBE, 0), 0, "probe before start");
        assert_eq!(send(window, ipc::SHOW_SETTINGS, 0), 0);
        assert_eq!(send(window, ipc::QUIT, 0), ipc::QUIT_REFUSED, "quit before start");
        assert!(!is_visible(window), "show request before start is ignored");

        app.start(Action::Background).unwrap();
        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);
        // SAFETY: WM_GETICON takes no pointers.
        let icon = unsafe { SendMessageW(window, WM_GETICON, ICON_SMALL as usize, 0) };
        assert_ne!(icon, 0, "DPI-specific window icon is set");
        assert!(!is_visible(window), "background start stays hidden");
        let source = IniFile::new(&ini);
        assert_eq!(Config::load(&source, &folders), config, "settings written at start");

        let hotkey_edit = Control::child(window, id::RECORDING_HOTKEY);
        hotkey_edit.set_text("Alt+");
        command(window, id::SAVE);
        assert_eq!(status(window), strings::status::INVALID_HOTKEY);

        hotkey_edit.set_text("Shift+R");
        command(window, id::SAVE);
        assert_eq!(status(window), strings::status::SHIFT_ONLY_HOTKEY);

        hotkey_edit.set_text("Strg+Alt+Shift+F23");
        command(window, id::SAVE);
        assert_eq!(status(window), strings::status::DUPLICATE_HOTKEY);

        hotkey_edit.set_text("Strg+Alt+Shift+F22");
        Control::child(window, id::COUNTDOWN).select(2);
        command(window, IDOK); // Enter
        assert_eq!(status(window), strings::status::SAVED);
        assert_eq!(source.string("recording_hotkey", OsStr::new("")), "Strg+Alt+Shift+F22");

        // Owner-drawn buttons also report double clicks; those must not save again.
        hotkey_edit.set_text("Alt+");
        let save_button = Control::child(window, id::SAVE);
        let double_click =
            (usize::try_from(BN_DOUBLECLICKED).unwrap() << 16) | usize::try_from(id::SAVE).unwrap();
        // SAFETY: WM_COMMAND carries the button handle, no pointers to memory.
        unsafe { SendMessageW(window, WM_COMMAND, double_click, save_button.handle() as LPARAM) };
        assert_eq!(status(window), strings::status::SAVED, "double click is not a save");
        hotkey_edit.set_text("Strg+Alt+Shift+F22");
        assert_eq!(source.int("countdown", 0), 5);
        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);

        assert_eq!(send(window, ipc::AUDIO_SETTINGS, 1), 1);
        assert_eq!(source.int("microphone", 0), 1);
        assert_eq!(source.int("system_audio", 1), 0);
        assert!(Control::child(window, id::MICROPHONE).is_checked());

        let status_control = Control::child(window, id::STATUS);
        let width_at_96 = client_width(status_control.handle());
        let suggested = RECT { left: 10, top: 10, right: 1600, bottom: 1500 };
        // SAFETY: WM_DPICHANGED reads the RECT behind lParam, which outlives the call.
        unsafe {
            SendMessageW(window, WM_DPICHANGED, 0x00C0_00C0, (&raw const suggested) as LPARAM)
        };
        assert_eq!(app.dpi(), Dpi::new(192));
        assert_eq!(
            client_width(status_control.handle()),
            Dpi::new(192).scale(width_at_96),
            "controls follow the DPI"
        );

        send(window, ipc::SHOW_SETTINGS, 0);
        assert!(is_visible(window));
        // A second request must not overwrite unsaved edits in the visible window.
        hotkey_edit.set_text("Strg+Alt+K");
        send(window, ipc::SHOW_RECORDING_SETTINGS, 0);
        assert_eq!(hotkey_edit.text(), "Strg+Alt+K", "unsaved edit kept");
        command(window, IDCANCEL); // Escape
        assert!(!is_visible(window), "escape hides");
        send(window, ipc::SHOW_RECORDING_SETTINGS, 0);
        assert_eq!(
            hotkey_edit.text(),
            "Strg+Alt+Shift+F22",
            "a hidden window is refilled from the saved settings"
        );
        send(window, WM_CLOSE, 0);
        assert!(!is_visible(window), "closing only hides");

        // No engine runs next to the test executable, so nothing blocks quitting.
        assert_eq!(send(window, ipc::QUIT, 0), ipc::QUIT_ACCEPTED);
        // SAFETY: no pointer arguments.
        assert_eq!(unsafe { IsWindow(window) }, 0, "quit destroys the window");
    }
}
