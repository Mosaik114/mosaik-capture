//! Settings storage: where `settings.ini` lives, reading it through the Win32 profile
//! API and replacing it atomically.
//!
//! The file is UTF-16LE with a BOM. It is shared with the capture engine, which locates
//! it the same way (or through `MOSAIK_CAPTURE_TEST_CONFIG` in preview mode).

use std::env;
use std::ffi::OsString;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};

use windows_sys::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
};
use windows_sys::Win32::System::WindowsProgramming::{
    GetPrivateProfileIntW, GetPrivateProfileStringW,
};
use windows_sys::Win32::UI::Shell::{
    CSIDL_MYPICTURES, CSIDL_MYVIDEO, SHGFP_TYPE_CURRENT, SHGetFolderPathW,
};

use crate::config::{Config, DefaultFolders, ProfileSource, SECTION};
use crate::wide::{from_wide, to_wide};

/// Environment variable through which the engine (started by us) finds a preview
/// settings file.
pub const PREVIEW_CONFIG_VAR: &str = "MOSAIK_CAPTURE_TEST_CONFIG";
/// Folder below `%LOCALAPPDATA%` that holds the settings.
const APP_FOLDER: &str = "Mosaiks-Aufnahme";
/// File name of the settings.
const FILE_NAME: &str = "settings.ini";
/// Byte-order mark that makes the profile API treat the file as UTF-16LE.
const UTF16LE_BOM: [u8; 2] = [0xFF, 0xFE];
/// `MAX_PATH`, the buffer size `SHGetFolderPathW` requires.
const MAX_PATH: usize = 260;

/// The settings file and the operations on it.
#[derive(Clone, Debug)]
pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    /// Uses `%LOCALAPPDATA%\Mosaiks-Aufnahme\settings.ini`, creating the folder if needed.
    pub fn user_default() -> Self {
        let folder = default_folder(&env::var_os("LOCALAPPDATA").unwrap_or_default());
        // Like the original, ignore errors: if the folder cannot be created, saving
        // fails later and is reported in the window.
        let _ = fs::create_dir(&folder);
        Self::at(folder.join(FILE_NAME))
    }

    /// Uses an explicit file, e.g. for `--preview`.
    pub fn at(path: PathBuf) -> Self {
        Self { path }
    }

    /// The settings file.
    pub fn path(&self) -> &Path {
        &self.path
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

/// `%LOCALAPPDATA%\Mosaiks-Aufnahme` for the given `LOCALAPPDATA` value.
fn default_folder(local_app_data: &std::ffi::OsStr) -> PathBuf {
    Path::new(local_app_data).join(APP_FOLDER)
}

/// Default output folders below the user's "Pictures" and "Videos" folders.
pub fn known_default_folders() -> DefaultFolders {
    DefaultFolders::new(
        &known_folder(CSIDL_MYPICTURES),
        &known_folder(CSIDL_MYVIDEO),
    )
}

/// Looks up a shell folder; an empty string if that fails.
fn known_folder(csidl: u32) -> String {
    let mut buffer = [0u16; MAX_PATH];
    let Ok(csidl) = i32::try_from(csidl) else {
        return String::new();
    };
    // SAFETY: `buffer` holds MAX_PATH characters as the API requires; null window and
    // token handles are documented as valid.
    let result = unsafe {
        SHGetFolderPathW(
            std::ptr::null_mut(),
            csidl,
            std::ptr::null_mut(),
            // The flag is a small enum value; the API takes it as u32.
            SHGFP_TYPE_CURRENT.cast_unsigned(),
            buffer.as_mut_ptr(),
        )
    };
    if result < 0 {
        return String::new();
    }
    from_wide(&buffer)
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
        Self {
            path: to_wide(path),
            section: to_wide(SECTION),
        }
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

/// Writes `contents` as UTF-16LE with BOM to `<path>.new`, then atomically replaces
/// `path` with it (write-through, so the rename hits the disk before returning).
pub fn write_atomic(path: &Path, contents: &str) -> io::Result<()> {
    let mut temp = OsString::from(path);
    temp.push(".new");
    let temp = PathBuf::from(temp);

    let mut bytes = Vec::with_capacity(2 + 2 * contents.len());
    bytes.extend_from_slice(&UTF16LE_BOM);
    bytes.extend(contents.encode_utf16().flat_map(u16::to_le_bytes));

    OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .share_mode(0)
        .open(&temp)?
        .write_all(&bytes)?;

    let (from, to) = (to_wide(&temp), to_wide(path));
    // SAFETY: both paths are NUL-terminated and outlive the call.
    let moved = unsafe {
        MoveFileExW(
            from.as_ptr(),
            to.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if moved == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
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
    fn default_folder_is_below_local_app_data() {
        let folder = default_folder("C:\\Users\\Ada\\AppData\\Local".as_ref());
        assert_eq!(
            folder,
            Path::new("C:\\Users\\Ada\\AppData\\Local\\Mosaiks-Aufnahme")
        );
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
        assert!(
            !dir.file("settings.ini.new").exists(),
            "temp file is moved away"
        );
    }

    #[test]
    fn saved_file_is_utf16le_with_bom() {
        let dir = TempDir::new();
        let path = dir.file("settings.ini");
        let config = Config::defaults(&sample_folders());
        write_atomic(&path, &config.to_ini()).unwrap();
        let bytes = fs::read(&path).unwrap();
        assert_eq!(bytes[..2], UTF16LE_BOM);
        let units: Vec<u16> = bytes[2..]
            .chunks_exact(2)
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
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
        assert_eq!(
            Config::load(&IniFile::new(&path), &sample_folders()),
            config
        );
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
