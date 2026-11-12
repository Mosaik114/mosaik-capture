//! The primary instance: settings window, tray icon and message loop.

mod app;
mod controls;
mod folder_picker;
mod layout;
mod strings;
mod theme;
mod tray;

use std::{env, mem, ptr};

use windows_sys::Win32::Foundation::{FALSE, HWND, LPARAM, LRESULT, RECT, TRUE, WPARAM};
use windows_sys::Win32::Graphics::Dwm::{DWMWA_USE_IMMERSIVE_DARK_MODE, DwmSetWindowAttribute};
use windows_sys::Win32::System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::HiDpi::{
    AdjustWindowRectExForDpi, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForSystem,
    GetDpiForWindow, SetProcessDpiAwarenessContext,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DispatchMessageW,
    GWLP_USERDATA, GetMessageW, GetWindowLongPtrW, IDC_ARROW, IsDialogMessageW, LoadCursorW, MSG,
    RegisterClassW, SPI_GETWORKAREA, SetWindowLongPtrW, SystemParametersInfoW, TranslateMessage,
    WINDOW_EX_STYLE, WINDOW_STYLE, WM_NCCREATE, WM_NCDESTROY, WNDCLASSW, WS_CAPTION,
    WS_EX_APPWINDOW, WS_MINIMIZEBOX, WS_SYSMENU,
};

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

/// Why the window could not be brought up.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StartupError {
    /// `RegisterClassW` failed.
    RegisterClass,
    /// `CreateWindowExW` failed.
    CreateWindow,
}

/// Runs the primary instance until the user quits.
pub fn run(invocation: &Invocation) -> Result<(), StartupError> {
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
    // SAFETY: no arguments. Only the initial guess; see `create_window`.
    let dpi = Dpi::new(unsafe { GetDpiForSystem() });
    let exe = env::current_exe().unwrap_or_default();
    let app = App::new(instance, dpi, store, config, exe, preview);

    let class_name = to_wide(ipc::WINDOW_CLASS);
    register_class(&app, &class_name)?;
    let window = create_window(&app, &class_name)?;

    app.start(invocation.action);
    run_message_loop(window);

    // The window is normally gone by now. If the loop ended for another reason,
    // detach `app` so the window procedure cannot see it after it is dropped.
    // SAFETY: clearing user data on a (possibly destroyed) window is harmless.
    unsafe { SetWindowLongPtrW(window, GWLP_USERDATA, 0) };
    Ok(())
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

fn register_class(app: &App, class_name: &[u16]) -> Result<(), StartupError> {
    let class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        cbClsExtra: 0,
        cbWndExtra: 0,
        hInstance: app.instance(),
        hIcon: tray::app_icon(app.instance()),
        // SAFETY: loads a predefined system cursor.
        hCursor: unsafe { LoadCursorW(ptr::null_mut(), IDC_ARROW) },
        hbrBackground: app.brushes().background(),
        lpszMenuName: ptr::null(),
        lpszClassName: class_name.as_ptr(),
    };
    // SAFETY: `class` and the class name it points to outlive the call.
    if unsafe { RegisterClassW(&raw const class) } == 0 {
        return Err(StartupError::RegisterClass);
    }
    Ok(())
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
fn centered_frame(dpi: Dpi) -> RECT {
    let mut area = RECT { left: 0, top: 0, right: 0, bottom: 0 };
    // SAFETY: SPI_GETWORKAREA writes one RECT into `area`, which outlives the call.
    unsafe { SystemParametersInfoW(SPI_GETWORKAREA, 0, (&raw mut area).cast(), 0) };
    let (width, height) = frame_size(dpi);
    let (x, y) = layout::centered([area.left, area.top, area.right, area.bottom], width, height);
    RECT { left: x, top: y, right: x + width, bottom: y + height }
}

/// Creates the (hidden) settings window, sized for the DPI of the monitor it lands on.
fn create_window(app: &App, class_name: &[u16]) -> Result<HWND, StartupError> {
    let frame = centered_frame(app.dpi());
    let title = to_wide(strings::WINDOW_TITLE);
    // SAFETY: class name and title are NUL-terminated and outlive the call. `app`
    // outlives the window (see `run`), so handing its address to WM_NCCREATE is sound.
    let window = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE,
            class_name.as_ptr(),
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
        return Err(StartupError::CreateWindow);
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
    if actual != app.dpi() {
        app.apply_dpi(actual, Some(centered_frame(actual)));
    }
    Ok(window)
}

/// Pumps messages until `WM_QUIT`. `IsDialogMessageW` gives the window dialog-style
/// keyboard handling: Tab/arrow navigation, Enter (`IDOK`) and Escape (`IDCANCEL`).
fn run_message_loop(window: HWND) {
    // SAFETY: MSG is a plain C struct for which all-zero is valid.
    let mut message: MSG = unsafe { mem::zeroed() };
    // SAFETY: `message` outlives every call that reads or writes it.
    unsafe {
        while GetMessageW(&raw mut message, ptr::null_mut(), 0, 0) > 0 {
            if IsDialogMessageW(window, &raw const message) == 0 {
                TranslateMessage(&raw const message);
                DispatchMessageW(&raw const message);
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
        // SAFETY: for WM_NCCREATE, lParam points to the CREATESTRUCTW whose
        // lpCreateParams is the `&App` passed to CreateWindowExW in `create_window`.
        let app = unsafe { (*(lparam as *const CREATESTRUCTW)).lpCreateParams.cast::<App>() };
        // SAFETY: `window` is the window being created; storing a pointer is plain data.
        unsafe { SetWindowLongPtrW(window, GWLP_USERDATA, app as isize) };
        // SAFETY: `app` points to the live App on `run`'s stack.
        unsafe { (*app).attach(window) };
    }

    // SAFETY: reads plain data from our own window.
    let app = unsafe { GetWindowLongPtrW(window, GWLP_USERDATA) } as *const App;
    // SAFETY: a non-null value was stored above and points to the App on `run`'s stack,
    // which outlives the window; it is cleared on WM_NCDESTROY and after the loop.
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
    use layout::id;
    use std::path::PathBuf;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        DestroyWindow, GetClientRect, IDCANCEL, IDOK, IsWindowVisible, SendMessageW, WM_CLOSE,
        WM_COMMAND, WM_DPICHANGED,
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
        controls::text(controls::child(window, id::STATUS))
    }

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

    /// Drives the real settings window: start-up, probe, validation, saving, keyboard
    /// commands, a DPI change and the engine's audio message.
    ///
    /// It only touches this process (a private window class, a temp settings file in
    /// preview mode, no engine, no registry), but it creates a window and registers
    /// two unusual global hotkeys, so it is opt-in: `cargo test -- --ignored`.
    #[test]
    #[ignore = "creates a window and global hotkeys; run with `cargo test -- --ignored`"]
    fn settings_window_end_to_end() {
        let dir = env::temp_dir().join(format!("mosaik-capture-ui-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let ini = dir.join("settings.ini");
        let folders = DefaultFolders::new(r"C:\Bilder", r"C:\Videos");
        let mut config = Config::defaults(&folders);
        config.screenshot_hotkey = "Strg+Alt+Shift+F23".to_owned();
        config.recording_hotkey = "Strg+Alt+Shift+F24".to_owned();
        config.autostart = false;

        // SAFETY: a null name returns the handle of the executable itself.
        let instance = unsafe { GetModuleHandleW(ptr::null()) };
        let app = App::new(
            instance,
            Dpi::new(96),
            SettingsStore::at(ini.clone()),
            config.clone(),
            env::current_exe().unwrap(),
            Some(&ini),
        );
        let class_name = to_wide("MosaikCapture.Host.Test");
        register_class(&app, &class_name).unwrap();
        let window = create_window(&app, &class_name).unwrap();
        let _cleanup = Cleanup { window, dir: dir.clone() };
        // Whatever monitor the window landed on, lay it out for 96 DPI first.
        app.apply_dpi(Dpi::new(96), None);
        app.start(Action::Background);

        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);
        assert!(!is_visible(window), "background start stays hidden");
        let source = IniFile::new(&ini);
        assert_eq!(Config::load(&source, &folders), config, "settings written at start");

        let hotkey_edit = controls::child(window, id::RECORDING_HOTKEY);
        controls::set_text(hotkey_edit, "Alt+");
        command(window, id::SAVE);
        assert_eq!(status(window), strings::status::INVALID_HOTKEY);

        controls::set_text(hotkey_edit, "Strg+Alt+Shift+F23");
        command(window, id::SAVE);
        assert_eq!(status(window), strings::status::DUPLICATE_HOTKEY);

        controls::set_text(hotkey_edit, "Strg+Alt+Shift+F22");
        controls::select(controls::child(window, id::COUNTDOWN), 2);
        command(window, IDOK); // Enter
        assert_eq!(status(window), strings::status::SAVED);
        assert_eq!(source.string("recording_hotkey", ""), "Strg+Alt+Shift+F22");
        assert_eq!(source.int("countdown", 0), 5);
        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);

        assert_eq!(send(window, ipc::AUDIO_SETTINGS, 1), 1);
        assert_eq!(source.int("microphone", 0), 1);
        assert_eq!(source.int("system_audio", 1), 0);
        assert!(controls::is_checked(controls::child(window, id::MICROPHONE)));

        let status_control = controls::child(window, id::STATUS);
        assert_eq!(client_width(status_control), 675);
        let suggested = RECT { left: 10, top: 10, right: 1600, bottom: 1500 };
        // SAFETY: WM_DPICHANGED reads the RECT behind lParam, which outlives the call.
        unsafe {
            SendMessageW(window, WM_DPICHANGED, 0x00C0_00C0, (&raw const suggested) as LPARAM)
        };
        assert_eq!(app.dpi(), Dpi::new(192));
        assert_eq!(client_width(status_control), 1350, "controls follow the DPI");

        send(window, ipc::SHOW_SETTINGS, 0);
        assert!(is_visible(window));
        command(window, IDCANCEL); // Escape
        assert!(!is_visible(window), "escape hides");
        send(window, ipc::SHOW_RECORDING_SETTINGS, 0);
        send(window, WM_CLOSE, 0);
        assert!(!is_visible(window), "closing only hides");
    }
}
