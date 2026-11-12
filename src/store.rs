//! Settings storage: where `settings.ini` lives, reading it through the Win32 profile
//! API and replacing it atomically.
//!
//! The file is UTF-16LE with a BOM. It is shared with the capture engine, which locates
//! it the same way (or through [`PREVIEW_CONFIG_VAR`] in preview mode).

use std::ffi::OsString;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::time::Duration;
use std::{env, ptr, thread};

use windows_sys::Win32::Foundation::{ERROR_LOCK_VIOLATION, ERROR_SHARING_VIOLATION};
use windows_sys::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
};
use windows_sys::Win32::System::Com::CoTaskMemFree;
use windows_sys::Win32::System::WindowsProgramming::{
    GetPrivateProfileIntW, GetPrivateProfileStringW,
};
use windows_sys::Win32::UI::Shell::{
    FOLDERID_LocalAppData, FOLDERID_Pictures, FOLDERID_Videos, KF_FLAG_DEFAULT,
    SHGetKnownFolderPath,
};
use windows_sys::core::GUID;

use crate::config::{Config, DefaultFolders, ProfileSource, SECTION};
use crate::wide::{from_wide, from_wide_ptr, to_wide};

/// Environment variable through which the engine (started by us) finds a preview
/// settings file.
pub const PREVIEW_CONFIG_VAR: &str = "MOSAIK_CAPTURE_TEST_CONFIG";
/// Folder below `%LOCALAPPDATA%` that holds the settings.
const APP_FOLDER: &str = "MosaikCapture";
/// Folder used by earlier versions; its settings are copied over once.
const LEGACY_APP_FOLDER: &str = "Mosaiks-Aufnahme";
/// File name of the settings.
const FILE_NAME: &str = "settings.ini";
/// How often a write is attempted while another process (e.g. the engine reading the
/// settings) briefly holds the file open.
const SHARING_ATTEMPTS: u32 = 5;
/// Pause between two attempts.
const SHARING_RETRY_DELAY: Duration = Duration::from_millis(50);
/// Byte-order mark that makes the profile API treat the file as UTF-16LE.
const UTF16LE_BOM: [u8; 2] = [0xFF, 0xFE];

/// The settings file and the operations on it.
#[derive(Clone, Debug)]
pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    /// Uses `%LOCALAPPDATA%\MosaikCapture\settings.ini`, creating the folder and
    /// migrating the settings of earlier versions if needed.
    pub fn user_default() -> Self {
        let local_app_data = local_app_data(
            env::var_os("LOCALAPPDATA"),
            || known_folder(&FOLDERID_LocalAppData),
            env::temp_dir,
        );
        Self::at(prepare_user_folder(&local_app_data))
    }

    /// Uses an explicit file, e.g. for `--preview`.
    pub fn at(path: PathBuf) -> Self {
        Self { path }
    }

    /// Reads the settings; missing keys (or a missing file) yield defaults.
    pub fn load(&self) -> Config {
        Config::load(&IniFile::new(&self.path), &known_default_folders())
    }

    /// Writes all settings to a temporary file and moves it over the old one.
    pub fn save(&self, config: &Config) -> io::Result<()> {
        write_atomic(&self.path, &config.to_ini())
    }
}

/// The local application-data folder: `%LOCALAPPDATA%` if it is an absolute path,
/// else the known folder, else (never expected) the temp folder. The result is always
/// absolute, so settings never end up relative to the working directory.
fn local_app_data(
    variable: Option<OsString>,
    known: impl FnOnce() -> String,
    temp: impl FnOnce() -> PathBuf,
) -> PathBuf {
    if let Some(path) = variable.map(PathBuf::from).filter(|path| path.is_absolute()) {
        return path;
    }
    let known = PathBuf::from(known());
    if known.is_absolute() { known } else { temp() }
}

/// Creates `<local_app_data>\MosaikCapture` and, once, copies the settings of
/// earlier versions into it. Returns the settings file.
///
/// Errors are ignored on purpose: without the folder, saving fails later and the
/// window reports it; a failed migration just means starting with defaults.
fn prepare_user_folder(local_app_data: &Path) -> PathBuf {
    let folder = local_app_data.join(APP_FOLDER);
    let file = folder.join(FILE_NAME);
    let _ = fs::create_dir(&folder);
    let legacy = local_app_data.join(LEGACY_APP_FOLDER).join(FILE_NAME);
    if let Some(source) = migration_source(&file, &legacy, Path::is_file) {
        // The old file stays where it is, so downgrading keeps working.
        let _ = fs::copy(source, &file);
    }
    file
}

/// The legacy file to copy: only if the new file is missing and the legacy one exists.
fn migration_source<'a>(
    file: &Path,
    legacy: &'a Path,
    exists: impl Fn(&Path) -> bool,
) -> Option<&'a Path> {
    (!exists(file) && exists(legacy)).then_some(legacy)
}

/// Default output folders below the user's "Pictures" and "Videos" folders.
pub fn known_default_folders() -> DefaultFolders {
    DefaultFolders::new(&known_folder(&FOLDERID_Pictures), &known_folder(&FOLDERID_Videos))
}

/// Looks up a known folder (any length); an empty string if that fails.
fn known_folder(id: &GUID) -> String {
    let mut path = ptr::null_mut();
    // SAFETY: `id` is a valid GUID; `path` receives a COM-allocated string.
    let result = unsafe {
        SHGetKnownFolderPath(id, KF_FLAG_DEFAULT.cast_unsigned(), ptr::null_mut(), &raw mut path)
    };
    let folder = if result >= 0 {
        // SAFETY: on success `path` is a NUL-terminated string.
        unsafe { from_wide_ptr(path) }
    } else {
        String::new()
    };
    // SAFETY: the string must be freed with the COM allocator, even on failure.
    unsafe { CoTaskMemFree(path.cast()) };
    folder
}

/// Read access to one INI file via `GetPrivateProfile*W`, which handles encodings,
/// case-insensitive names, whitespace and quoting exactly like Windows always has.
#[derive(Debug)]
pub struct IniFile {
    path: Vec<u16>,
    section: Vec<u16>,
}

impl IniFile {
    /// Opens (lazily) the file at `path`.
    pub fn new(path: &Path) -> Self {
        Self { path: to_wide(path), section: to_wide(SECTION) }
    }
}

impl ProfileSource for IniFile {
    fn string(&self, key: &str, default: &str) -> String {
        let key = to_wide(key);
        let default = to_wide(default);
        let mut buffer = vec![0u16; 512];
        loop {
            let capacity = u32::try_from(buffer.len()).unwrap_or(u32::MAX);
            // SAFETY: all string pointers are NUL-terminated and outlive the call;
            // `capacity` is the true length of `buffer`.
            let copied = unsafe {
                GetPrivateProfileStringW(
                    self.section.as_ptr(),
                    key.as_ptr(),
                    default.as_ptr(),
                    buffer.as_mut_ptr(),
                    capacity,
                    self.path.as_ptr(),
                )
            };
            // A result of `capacity - 1` means the value was truncated.
            if copied + 1 < capacity || buffer.len() >= 1 << 16 {
                return from_wide(&buffer);
            }
            buffer.resize(buffer.len() * 2, 0);
        }
    }

    fn int(&self, key: &str, default: u32) -> u32 {
        let key = to_wide(key);
        // SAFETY: all string pointers are NUL-terminated and outlive the call.
        unsafe {
            GetPrivateProfileIntW(
                self.section.as_ptr(),
                key.as_ptr(),
                default.cast_signed(),
                self.path.as_ptr(),
            )
        }
        // Same bits as the documented UINT result.
        .cast_unsigned()
    }
}

/// Writes `contents` as UTF-16LE with BOM to `<path>.new`, flushes it to disk, then
/// atomically replaces `path` with it (write-through, so the rename is durable, too).
///
/// Sharing violations, which occur while another process reads the file, are retried
/// briefly. On failure the temporary file is removed and `path` is left untouched.
pub fn write_atomic(path: &Path, contents: &str) -> io::Result<()> {
    let mut temp = OsString::from(path);
    temp.push(".new");
    let temp = PathBuf::from(temp);

    let mut bytes = Vec::with_capacity(2 + 2 * contents.len());
    bytes.extend_from_slice(&UTF16LE_BOM);
    bytes.extend(contents.encode_utf16().flat_map(u16::to_le_bytes));

    let result = retry_while_shared(|| write_synced(&temp, &bytes))
        .and_then(|()| retry_while_shared(|| replace(&temp, path)));
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result
}

/// Creates `path` exclusively with `bytes` and flushes it to disk, so a crash after the
/// rename cannot leave an empty settings file.
fn write_synced(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let mut file =
        OpenOptions::new().write(true).create(true).truncate(true).share_mode(0).open(path)?;
    file.write_all(bytes)?;
    file.sync_all()
}

/// Atomically moves `from` over `to`.
fn replace(from: &Path, to: &Path) -> io::Result<()> {
    let (from, to) = (to_wide(from), to_wide(to));
    // SAFETY: both paths are NUL-terminated and outlive the call.
    let moved = unsafe {
        MoveFileExW(from.as_ptr(), to.as_ptr(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)
    };
    if moved == 0 { Err(io::Error::last_os_error()) } else { Ok(()) }
}

/// Runs `operation` again (a few times, after a short pause) while it fails because
/// another process has the file open.
fn retry_while_shared<T>(mut operation: impl FnMut() -> io::Result<T>) -> io::Result<T> {
    let mut attempt = 1;
    loop {
        match operation() {
            Err(error) if attempt < SHARING_ATTEMPTS && is_sharing_violation(&error) => {
                attempt += 1;
                thread::sleep(SHARING_RETRY_DELAY);
            }
            result => return result,
        }
    }
}

fn is_sharing_violation(error: &io::Error) -> bool {
    let code = error.raw_os_error().and_then(|code| u32::try_from(code).ok());
    matches!(code, Some(ERROR_SHARING_VIOLATION | ERROR_LOCK_VIOLATION))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Countdown, FrameRate};
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A unique scratch directory under the system temp folder, removed on drop.
    struct TempDir(PathBuf);

    impl TempDir {
        fn new() -> Self {
            static NEXT: AtomicU32 = AtomicU32::new(0);
            let name = format!(
                "mosaik-capture-test-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            );
            let path = env::temp_dir().join(name);
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn file(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn sample_folders() -> DefaultFolders {
        DefaultFolders::new("C:\\Bilder", "C:\\Videos")
    }

    fn write_utf16(path: &Path, text: &str) {
        let mut bytes = UTF16LE_BOM.to_vec();
        bytes.extend(text.encode_utf16().flat_map(u16::to_le_bytes));
        fs::write(path, bytes).unwrap();
    }

    #[test]
    fn local_app_data_is_always_absolute() {
        let known = || r"C:\Users\Ada\AppData\Local".to_owned();
        let temp = || PathBuf::from(r"C:\Temp");
        let from_env = local_app_data(Some(r"D:\Local".into()), known, temp);
        assert_eq!(from_env, Path::new(r"D:\Local"));
        let missing = local_app_data(None, known, temp);
        assert_eq!(missing, Path::new(r"C:\Users\Ada\AppData\Local"));
        let relative = local_app_data(Some("relative".into()), known, temp);
        assert_eq!(relative, Path::new(r"C:\Users\Ada\AppData\Local"));
        let nothing = local_app_data(Some("".into()), String::new, temp);
        assert_eq!(nothing, Path::new(r"C:\Temp"));
    }

    #[test]
    fn sharing_violations_are_retried_briefly() {
        let mut calls = 0;
        let result = retry_while_shared(|| {
            calls += 1;
            if calls < 3 {
                Err(io::Error::from_raw_os_error(ERROR_SHARING_VIOLATION.cast_signed()))
            } else {
                Ok(calls)
            }
        });
        assert_eq!(result.unwrap(), 3);

        let mut calls = 0;
        let result: io::Result<()> = retry_while_shared(|| {
            calls += 1;
            Err(io::Error::from_raw_os_error(ERROR_LOCK_VIOLATION.cast_signed()))
        });
        assert!(result.is_err());
        assert_eq!(calls, SHARING_ATTEMPTS, "gives up eventually");

        let mut calls = 0;
        let result: io::Result<()> = retry_while_shared(|| {
            calls += 1;
            Err(io::ErrorKind::NotFound.into())
        });
        assert!(result.is_err());
        assert_eq!(calls, 1, "other errors are not retried");
    }

    #[test]
    fn failed_write_leaves_no_temp_file() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        write_utf16(&path, "[capture]\r\nfps=30\r\n");
        // Hold the target open without sharing: the rename keeps failing.
        let blocker = OpenOptions::new().read(true).share_mode(0).open(&path).unwrap();
        let result = write_atomic(&path, "[capture]\r\nfps=60\r\n");
        drop(blocker);
        assert!(result.is_err());
        assert!(!dir.file("settings.ini.new").exists(), "temp file is cleaned up");
        assert_eq!(IniFile::new(&path).int("fps", 0), 30, "old settings untouched");
    }

    #[test]
    fn migration_decision() {
        let (file, legacy) = (Path::new("new.ini"), Path::new("old.ini"));
        let only = |present: &'static [&'static str]| {
            move |path: &Path| present.iter().any(|p| path == Path::new(p))
        };
        assert_eq!(migration_source(file, legacy, only(&["old.ini"])), Some(legacy));
        assert_eq!(migration_source(file, legacy, only(&["new.ini", "old.ini"])), None);
        assert_eq!(migration_source(file, legacy, only(&["new.ini"])), None);
        assert_eq!(migration_source(file, legacy, only(&[])), None);
    }

    #[test]
    fn user_folder_is_created_and_legacy_settings_are_copied_once() {
        let dir = TempDir::new();
        let legacy = dir.0.join("Mosaiks-Aufnahme").join("settings.ini");
        fs::create_dir_all(legacy.parent().unwrap()).unwrap();
        write_utf16(&legacy, "[capture]\r\nfps=30\r\n");

        let file = prepare_user_folder(&dir.0);
        assert_eq!(file, dir.0.join("MosaikCapture").join("settings.ini"));
        assert_eq!(fs::read(&file).unwrap(), fs::read(&legacy).unwrap());
        assert!(legacy.exists(), "the old file is kept");

        // A second start must not overwrite newer settings with the legacy file.
        write_utf16(&file, "[capture]\r\nfps=60\r\n");
        prepare_user_folder(&dir.0);
        assert_eq!(IniFile::new(&file).int("fps", 0), 60);
    }

    #[test]
    fn user_folder_without_legacy_settings() {
        let dir = TempDir::new();
        let file = prepare_user_folder(&dir.0);
        assert!(file.parent().unwrap().is_dir());
        assert!(!file.exists());
    }

    #[test]
    fn known_folders_resolve() {
        let folders = known_default_folders();
        assert!(folders.screenshots.ends_with(r"\Mosaiks-Screenshots"));
        assert!(folders.recordings.ends_with(r"\Mosaiks-Aufnahmen"));
        assert!(Path::new(&folders.screenshots).is_absolute(), "{}", folders.screenshots);
        assert!(Path::new(&folders.recordings).is_absolute(), "{}", folders.recordings);
    }

    #[test]
    fn missing_file_yields_defaults() {
        let dir = TempDir::new();
        let config = Config::load(&IniFile::new(&dir.file("absent.ini")), &sample_folders());
        assert_eq!(config, Config::defaults(&sample_folders()));
    }

    #[test]
    fn round_trip_through_real_file() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        let config = Config {
            screenshot_hotkey: "Strg + Umschalt + F12".to_owned(),
            recording_hotkey: "Alt+Leertaste".to_owned(),
            screenshot_folder: "D:\\Bilder\\Grüße".to_owned(),
            recording_folder: "\\\\nas\\videos".to_owned(),
            microphone: true,
            system_audio: false,
            show_clicks: false,
            autostart: false,
            frame_rate: FrameRate::Fps30,
            countdown: Countdown::Seconds5,
        };
        SettingsStore::at(path.clone()).save(&config).unwrap();
        let loaded = Config::load(&IniFile::new(&path), &sample_folders());
        assert_eq!(loaded, config);
        assert!(!dir.file("settings.ini.new").exists(), "temp file is moved away");
    }

    #[test]
    fn saved_file_is_utf16le_with_bom() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        let config = Config::defaults(&sample_folders());
        write_atomic(&path, &config.to_ini()).unwrap();
        let bytes = fs::read(&path).unwrap();
        assert_eq!(bytes[..2], UTF16LE_BOM);
        let units: Vec<u16> =
            bytes[2..].chunks_exact(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect();
        assert_eq!(String::from_utf16(&units).unwrap(), config.to_ini());
    }

    #[test]
    fn save_replaces_existing_file() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        write_utf16(&path, "[capture]\r\nfps=30\r\nextra=1\r\n");
        let store = SettingsStore::at(path.clone());
        let mut config = Config::defaults(&sample_folders());
        config.countdown = Countdown::Off;
        store.save(&config).unwrap();
        assert_eq!(Config::load(&IniFile::new(&path), &sample_folders()), config);
        assert_eq!(IniFile::new(&path).string("extra", "gone"), "gone");
    }

    #[test]
    fn hand_edited_values_are_normalised() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        write_utf16(
            &path,
            "[Capture]\r\n FPS = 45 \r\ncountdown=7\r\nmicrophone=yes\r\n\
             system_audio=0\r\nscreenshot_hotkey = \"Alt+1\" \r\n",
        );
        let config = Config::load(&IniFile::new(&path), &sample_folders());
        assert_eq!(config.frame_rate, FrameRate::Fps60);
        assert_eq!(config.countdown, Countdown::Seconds3);
        assert!(!config.microphone, "non-numeric reads as 0");
        assert!(!config.system_audio);
        assert!(config.show_clicks, "missing key keeps its default");
        assert_eq!(config.screenshot_hotkey, "Alt+1", "trimmed and unquoted");
    }

    #[test]
    fn long_values_are_read_completely() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        let long = format!("C:\\{}", "x".repeat(2000));
        write_utf16(&path, &format!("[capture]\r\nscreenshot_folder={long}\r\n"));
        assert_eq!(IniFile::new(&path).string("screenshot_folder", ""), long);
    }
}
