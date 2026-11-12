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
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForSystem, SetProcessDpiAwarenessContext,
};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::VK_ESCAPE;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AdjustWindowRectEx, CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW,
    DispatchMessageW, GWLP_USERDATA, GetMessageW, GetWindowLongPtrW, IDC_ARROW, IsDialogMessageW,
    LoadCursorW, MSG, RegisterClassW, SPI_GETWORKAREA, SW_HIDE, SetWindowLongPtrW, ShowWindow,
    SystemParametersInfoW, TranslateMessage, WM_KEYDOWN, WM_NCCREATE, WM_NCDESTROY, WNDCLASSW,
    WS_CAPTION, WS_EX_APPWINDOW, WS_MINIMIZEBOX, WS_SYSMENU,
};

use crate::cli::Invocation;
use crate::ipc;
use crate::store::{PREVIEW_CONFIG_VAR, SettingsStore};
use crate::wide::to_wide;
use app::App;
use layout::{CLIENT_HEIGHT, CLIENT_WIDTH, Dpi};

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
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let _com = ComApartment::enter();

    let store = match &invocation.preview {
        Some(path) => SettingsStore::at(path.clone()),
        None => SettingsStore::user_default(),
    };
    let mut config = store.load();
    let preview = invocation.preview.is_some();
    if preview {
        config.autostart = false;
        // Lets an engine started by us use the same preview file.
        // SAFETY: the process is single-threaded at this point, so nothing can read
        // the environment concurrently.
        unsafe { env::set_var(PREVIEW_CONFIG_VAR, store.path()) };
    }

    // SAFETY: a null name returns the handle of the executable itself.
    let instance = unsafe { GetModuleHandleW(ptr::null()) };
    // SAFETY: no arguments.
    let dpi = Dpi::new(unsafe { GetDpiForSystem() });
    let exe = env::current_exe().unwrap_or_default();
    let app = App::new(instance, dpi, store, config, exe, preview);

    let class_name = to_wide(ipc::WINDOW_CLASS);
    register_class(&app, &class_name)?;
    let window = create_window(&app, &class_name, dpi)?;

    app.start(invocation.action);
    run_message_loop(window);

    // The window is normally gone by now. If the loop ended for another reason,
    // detach `app` so the window procedure cannot see it after it is dropped.
    // SAFETY: clearing user data on a (possibly destroyed) window is harmless.
    unsafe { SetWindowLongPtrW(window, GWLP_USERDATA, 0) };
    Ok(())
}

/// COM single-threaded apartment for the current thread (needed by the folder
/// picker), left again on drop.
struct ComApartment;

impl ComApartment {
    fn enter() -> Self {
        // SAFETY: the reserved pointer must be null.
        unsafe { CoInitializeEx(ptr::null(), COINIT_APARTMENTTHREADED.cast_unsigned()) };
        Self
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        // SAFETY: balances CoInitializeEx on the same thread.
        unsafe { CoUninitialize() };
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
        hbrBackground: app.theme().background_brush(),
        lpszMenuName: ptr::null(),
        lpszClassName: class_name.as_ptr(),
    };
    // SAFETY: `class` and the class name it points to outlive the call.
    if unsafe { RegisterClassW(&raw const class) } == 0 {
        return Err(StartupError::RegisterClass);
    }
    Ok(())
}

fn create_window(app: &App, class_name: &[u16], dpi: Dpi) -> Result<HWND, StartupError> {
    let style = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    let ex_style = WS_EX_APPWINDOW;
    let mut frame = RECT {
        left: 0,
        top: 0,
        right: dpi.scale(CLIENT_WIDTH),
        bottom: dpi.scale(CLIENT_HEIGHT),
    };
    let mut area = RECT {
        left: 0,
        top: 0,
        right: 0,
        bottom: 0,
    };
    // SAFETY: both rectangles outlive the calls; SPI_GETWORKAREA writes one RECT.
    unsafe {
        AdjustWindowRectEx(&raw mut frame, style, FALSE, ex_style);
        SystemParametersInfoW(SPI_GETWORKAREA, 0, (&raw mut area).cast(), 0);
    }
    let width = frame.right - frame.left;
    let height = frame.bottom - frame.top;
    let (x, y) = layout::centered(
        [area.left, area.top, area.right, area.bottom],
        width,
        height,
    );

    let title = to_wide(strings::WINDOW_TITLE);
    // SAFETY: class name and title are NUL-terminated and outlive the call. `app`
    // outlives the window (see `run`), so handing its address to WM_NCCREATE is sound.
    let window = unsafe {
        CreateWindowExW(
            ex_style,
            class_name.as_ptr(),
            title.as_ptr(),
            style,
            x,
            y,
            width,
            height,
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
    Ok(window)
}

/// Pumps messages until `WM_QUIT`. Escape hides the window; Tab and arrow keys move
/// between controls like in a dialog.
fn run_message_loop(window: HWND) {
    // SAFETY: MSG is a plain C struct for which all-zero is valid.
    let mut message: MSG = unsafe { mem::zeroed() };
    // SAFETY: `message` outlives every call that reads or writes it.
    unsafe {
        while GetMessageW(&raw mut message, ptr::null_mut(), 0, 0) > 0 {
            if message.message == WM_KEYDOWN && message.wParam == usize::from(VK_ESCAPE) {
                ShowWindow(window, SW_HIDE);
                continue;
            }
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
        let app = unsafe {
            (*(lparam as *const CREATESTRUCTW))
                .lpCreateParams
                .cast::<App>()
        };
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
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        DestroyWindow, IsWindowVisible, SendMessageW, WM_CLOSE, WM_COMMAND,
    };

    fn send(window: HWND, message: u32, wparam: WPARAM) -> LRESULT {
        // SAFETY: the messages used here carry no pointers.
        unsafe { SendMessageW(window, message, wparam, 0) }
    }

    fn is_visible(window: HWND) -> bool {
        // SAFETY: no pointer arguments.
        unsafe { IsWindowVisible(window) != 0 }
    }

    fn status(window: HWND) -> String {
        controls::text(controls::child(window, id::STATUS))
    }

    /// Drives the real settings window: start-up, probe, validation, saving and the
    /// engine's audio message.
    ///
    /// It creates windows and registers (unusual) global hotkeys, so it is ignored by
    /// default. Run it on a separate, invisible desktop:
    /// `cargo test -- --ignored settings_window`.
    #[test]
    #[ignore = "creates real windows and global hotkeys; run on a hidden desktop"]
    fn settings_window_end_to_end() {
        let dir = std::env::temp_dir().join(format!("mosaik-capture-ui-{}", std::process::id()));
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
            std::env::current_exe().unwrap(),
            true,
        );
        let class_name = to_wide("MosaiksAufnahmeRustTest");
        register_class(&app, &class_name).unwrap();
        let window = create_window(&app, &class_name, Dpi::new(96)).unwrap();
        app.start(Action::Background);

        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);
        assert!(!is_visible(window), "background start stays hidden");
        let source = IniFile::new(&ini);
        assert_eq!(
            Config::load(&source, &folders),
            config,
            "settings written at start"
        );

        let hotkey_edit = controls::child(window, id::RECORDING_HOTKEY);
        controls::set_text(hotkey_edit, "Alt+");
        send(window, WM_COMMAND, usize::try_from(id::SAVE).unwrap());
        assert_eq!(status(window), strings::status::INVALID_HOTKEY);

        controls::set_text(hotkey_edit, "Strg+Alt+Shift+F23");
        send(window, WM_COMMAND, usize::try_from(id::SAVE).unwrap());
        assert_eq!(status(window), strings::status::DUPLICATE_HOTKEY);

        controls::set_text(hotkey_edit, "Strg+Alt+Shift+F22");
        controls::select(controls::child(window, id::COUNTDOWN), 2);
        send(window, WM_COMMAND, usize::try_from(id::SAVE).unwrap());
        assert_eq!(status(window), strings::status::SAVED);
        assert_eq!(source.string("recording_hotkey", ""), "Strg+Alt+Shift+F22");
        assert_eq!(source.int("countdown", 0), 5);
        assert_eq!(send(window, ipc::PROBE, 0), ipc::ProbeResponse::HEALTHY);

        assert_eq!(send(window, ipc::AUDIO_SETTINGS, 1), 1);
        assert_eq!(source.int("microphone", 0), 1);
        assert_eq!(source.int("system_audio", 1), 0);
        assert!(controls::is_checked(controls::child(
            window,
            id::MICROPHONE
        )));

        send(window, ipc::SHOW_SETTINGS, 0);
        assert!(is_visible(window));
        send(window, WM_CLOSE, 0);
        assert!(!is_visible(window), "closing only hides");

        // SAFETY: destroys the test window; `app` is still alive.
        unsafe { DestroyWindow(window) };
        let _ = std::fs::remove_dir_all(&dir);
    }
}
