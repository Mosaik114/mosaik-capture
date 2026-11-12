//! The user's capture settings: defaults, normalisation, validation and the INI
//! serialisation format.
//!
//! Everything here is pure. Reading and writing the actual file lives in
//! [`crate::store`], which feeds raw values in through [`ProfileSource`].

use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};

use crate::hotkey::{Hotkey, ParseHotkeyError};

/// INI section holding all settings.
pub const SECTION: &str = "capture";

/// INI key names.
pub mod keys {
    /// Hotkey that takes a screenshot.
    pub const SCREENSHOT_HOTKEY: &str = "screenshot_hotkey";
    /// Hotkey that starts or stops a recording.
    pub const RECORDING_HOTKEY: &str = "recording_hotkey";
    /// Folder for screenshots.
    pub const SCREENSHOT_FOLDER: &str = "screenshot_folder";
    /// Folder for recordings.
    pub const RECORDING_FOLDER: &str = "recording_folder";
    /// Record the microphone.
    pub const MICROPHONE: &str = "microphone";
    /// Record system audio.
    pub const SYSTEM_AUDIO: &str = "system_audio";
    /// Visualise mouse clicks in recordings.
    pub const SHOW_CLICKS: &str = "show_clicks";
    /// Start with Windows.
    pub const AUTOSTART: &str = "autostart";
    /// Recording frame rate.
    pub const FPS: &str = "fps";
    /// Countdown before a recording starts, in seconds.
    pub const COUNTDOWN: &str = "countdown";
}

/// Default screenshot hotkey.
pub const DEFAULT_SCREENSHOT_HOTKEY: &str = "Shift+Alt+4";
/// Default recording hotkey.
pub const DEFAULT_RECORDING_HOTKEY: &str = "Shift+Alt+5";

/// Characters that may not appear anywhere in a folder path.
const FORBIDDEN_PATH_CHARS: &[u8] = b"\n\r\"*<>?|";

/// Recording frame rate. Anything other than 30 is treated as 60.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FrameRate {
    /// 30 frames per second.
    Fps30,
    /// 60 frames per second.
    #[default]
    Fps60,
}

impl FrameRate {
    /// Interprets a stored value.
    pub const fn from_value(value: u32) -> Self {
        if value == 30 { Self::Fps30 } else { Self::Fps60 }
    }

    /// The value written to the settings file.
    pub const fn value(self) -> u32 {
        match self {
            Self::Fps30 => 30,
            Self::Fps60 => 60,
        }
    }

    /// Position in the frame-rate combo box ("30/60 Bilder/s", i.e. frames per second).
    pub const fn index(self) -> usize {
        match self {
            Self::Fps30 => 0,
            Self::Fps60 => 1,
        }
    }

    /// Interprets a combo-box selection; no selection means 60.
    pub const fn from_index(index: Option<usize>) -> Self {
        match index {
            Some(0) => Self::Fps30,
            _ => Self::Fps60,
        }
    }
}

/// Countdown before a recording starts. Unknown values fall back to three seconds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Countdown {
    /// Start immediately.
    Off,
    /// Three-second countdown.
    #[default]
    Seconds3,
    /// Five-second countdown.
    Seconds5,
}

impl Countdown {
    /// Interprets a stored value.
    pub const fn from_value(value: u32) -> Self {
        match value {
            0 => Self::Off,
            5 => Self::Seconds5,
            _ => Self::Seconds3,
        }
    }

    /// The value written to the settings file (the length in seconds).
    pub const fn value(self) -> u32 {
        match self {
            Self::Off => 0,
            Self::Seconds3 => 3,
            Self::Seconds5 => 5,
        }
    }

    /// Position in the countdown combo box ("Ohne Vorlauf" = no countdown, then 3 and
    /// 5 "Sekunden" = seconds).
    pub const fn index(self) -> usize {
        match self {
            Self::Off => 0,
            Self::Seconds3 => 1,
            Self::Seconds5 => 2,
        }
    }

    /// Interprets a combo-box selection; no selection means three seconds.
    pub const fn from_index(index: Option<usize>) -> Self {
        match index {
            Some(0) => Self::Off,
            Some(2) => Self::Seconds5,
            _ => Self::Seconds3,
        }
    }
}

/// Default output folders, which depend on the user's known folders.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefaultFolders {
    /// Default screenshot folder.
    pub screenshots: PathBuf,
    /// Default recording folder.
    pub recordings: PathBuf,
}

impl DefaultFolders {
    /// Builds the defaults from the user's "Pictures" and "Videos" folders. The
    /// subfolder is appended textually, so a failed lookup (empty path) yields the
    /// rooted, invalid `\Mosaiks-Screenshots` instead of a relative path.
    pub fn new(pictures: &Path, videos: &Path) -> Self {
        let below = |base: &Path, name: &str| {
            let mut path = OsString::from(base);
            path.push("\\");
            path.push(name);
            PathBuf::from(path)
        };
        Self {
            screenshots: below(pictures, "Mosaiks-Screenshots"),
            recordings: below(videos, "Mosaiks-Aufnahmen"),
        }
    }
}

/// Raw access to stored settings, with the semantics of the Win32 profile API:
/// a missing key yields the given default.
pub trait ProfileSource {
    /// Reads a string value exactly as stored (lossless UTF-16).
    fn string(&self, key: &str, default: &OsStr) -> OsString;
    /// Reads an unsigned integer value.
    fn int(&self, key: &str, default: u32) -> u32;
}

/// The two hotkeys of a configuration, both valid and distinct.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Hotkeys {
    /// Takes a screenshot.
    pub screenshot: Hotkey,
    /// Starts or stops a recording.
    pub recording: Hotkey,
}

/// Why a configuration cannot be applied.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConfigError {
    /// A hotkey could not be parsed.
    InvalidHotkey(ParseHotkeyError),
    /// Both actions use the same hotkey.
    DuplicateHotkey,
    /// A folder is not a complete, valid path.
    InvalidFolder,
}

/// All user settings.
#[expect(
    clippy::struct_excessive_bools,
    reason = "the flags are independent settings that mirror the INI file one to one"
)]
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Config {
    /// Hotkey text for screenshots, as entered by the user.
    pub screenshot_hotkey: String,
    /// Hotkey text for recordings, as entered by the user.
    pub recording_hotkey: String,
    /// Folder for screenshots (kept as a path: any valid Windows name round-trips).
    pub screenshot_folder: PathBuf,
    /// Folder for recordings.
    pub recording_folder: PathBuf,
    /// Record the microphone.
    pub microphone: bool,
    /// Record system audio.
    pub system_audio: bool,
    /// Visualise mouse clicks.
    pub show_clicks: bool,
    /// Start with Windows.
    pub autostart: bool,
    /// Recording frame rate.
    pub frame_rate: FrameRate,
    /// Countdown before recording.
    pub countdown: Countdown,
}

impl Config {
    /// The settings used when nothing has been stored yet.
    pub fn defaults(folders: &DefaultFolders) -> Self {
        Self {
            screenshot_hotkey: DEFAULT_SCREENSHOT_HOTKEY.to_owned(),
            recording_hotkey: DEFAULT_RECORDING_HOTKEY.to_owned(),
            screenshot_folder: folders.screenshots.clone(),
            recording_folder: folders.recordings.clone(),
            microphone: false,
            system_audio: true,
            show_clicks: true,
            autostart: true,
            frame_rate: FrameRate::default(),
            countdown: Countdown::default(),
        }
    }

    /// Loads settings, falling back to [`Config::defaults`] for missing keys and
    /// normalising out-of-range numbers.
    pub fn load(source: &impl ProfileSource, folders: &DefaultFolders) -> Self {
        let defaults = Self::defaults(folders);
        // Hotkeys are text; anything that is not valid UTF-16 cannot parse anyway.
        let text = |key, default: &str| {
            source.string(key, OsStr::new(default)).to_string_lossy().into_owned()
        };
        let path = |key, default: &Path| PathBuf::from(source.string(key, default.as_os_str()));
        let flag = |key, default: bool| source.int(key, u32::from(default)) != 0;
        Self {
            screenshot_hotkey: text(keys::SCREENSHOT_HOTKEY, &defaults.screenshot_hotkey),
            recording_hotkey: text(keys::RECORDING_HOTKEY, &defaults.recording_hotkey),
            screenshot_folder: path(keys::SCREENSHOT_FOLDER, &defaults.screenshot_folder),
            recording_folder: path(keys::RECORDING_FOLDER, &defaults.recording_folder),
            microphone: flag(keys::MICROPHONE, defaults.microphone),
            system_audio: flag(keys::SYSTEM_AUDIO, defaults.system_audio),
            show_clicks: flag(keys::SHOW_CLICKS, defaults.show_clicks),
            autostart: flag(keys::AUTOSTART, defaults.autostart),
            frame_rate: FrameRate::from_value(source.int(keys::FPS, defaults.frame_rate.value())),
            countdown: Countdown::from_value(
                source.int(keys::COUNTDOWN, defaults.countdown.value()),
            ),
        }
    }

    /// Serialises the settings in the exact layout `WritePrivateProfileStringW`
    /// produces for a fresh file, so the engine and older versions read it unchanged.
    /// Folders are written as stored, so the result is an `OsString`.
    pub fn to_ini(&self) -> OsString {
        let flag = |value: bool| OsString::from(if value { "1" } else { "0" });
        let entries = [
            (keys::SCREENSHOT_HOTKEY, OsString::from(&self.screenshot_hotkey)),
            (keys::RECORDING_HOTKEY, OsString::from(&self.recording_hotkey)),
            (keys::SCREENSHOT_FOLDER, self.screenshot_folder.clone().into_os_string()),
            (keys::RECORDING_FOLDER, self.recording_folder.clone().into_os_string()),
            (keys::MICROPHONE, flag(self.microphone)),
            (keys::SYSTEM_AUDIO, flag(self.system_audio)),
            (keys::SHOW_CLICKS, flag(self.show_clicks)),
            (keys::AUTOSTART, flag(self.autostart)),
            (keys::FPS, self.frame_rate.value().to_string().into()),
            (keys::COUNTDOWN, self.countdown.value().to_string().into()),
        ];
        let mut ini = OsString::from(format!("\r\n[{SECTION}]\r\n"));
        for (key, value) in entries {
            ini.push(key);
            ini.push("=");
            ini.push(quote_if_needed(value));
            ini.push("\r\n");
        }
        ini
    }

    /// Parses both hotkeys and checks that they differ.
    pub fn hotkeys(&self) -> Result<Hotkeys, ConfigError> {
        let parse = |text: &str| text.parse::<Hotkey>().map_err(ConfigError::InvalidHotkey);
        let screenshot = parse(&self.screenshot_hotkey)?;
        let recording = parse(&self.recording_hotkey)?;
        if screenshot == recording {
            return Err(ConfigError::DuplicateHotkey);
        }
        Ok(Hotkeys { screenshot, recording })
    }

    /// Full validation before saving: hotkeys first, then folders.
    pub fn validate(&self) -> Result<Hotkeys, ConfigError> {
        let hotkeys = self.hotkeys()?;
        if !is_valid_folder(&self.screenshot_folder) || !is_valid_folder(&self.recording_folder) {
            return Err(ConfigError::InvalidFolder);
        }
        Ok(hotkeys)
    }
}

/// Protects a value from the profile API's read-side cleanup.
///
/// `GetPrivateProfileStringW` trims spaces and tabs around a value and then removes
/// one pair of matching surrounding quotes (`"…"` or `'…'`). A value that would be
/// changed by that — leading or trailing whitespace, or already quoted — is written
/// in double quotes, so it reads back exactly as stored.
fn quote_if_needed(value: OsString) -> OsString {
    let units: Vec<u16> = value.encode_wide().collect();
    let is_blank = |unit: &u16| *unit == u16::from(b' ') || *unit == u16::from(b'\t');
    let is_quote = |unit: &u16| *unit == u16::from(b'"') || *unit == u16::from(b'\'');
    let padded = units.first().is_some_and(is_blank) || units.last().is_some_and(is_blank);
    let quoted =
        units.len() >= 2 && units.first().is_some_and(is_quote) && units.first() == units.last();
    if !padded && !quoted {
        return value;
    }
    let mut wrapped = OsString::from("\"");
    wrapped.push(value);
    wrapped.push("\"");
    wrapped
}

/// Whether `path` is an absolute drive path (`C:\…`, `C:/…`) or a UNC path (`\\…`)
/// without characters that Windows forbids in file names.
pub fn is_valid_folder(path: &Path) -> bool {
    let units: Vec<u16> = path.as_os_str().encode_wide().collect();
    let is = |index: usize, c: u8| units.get(index) == Some(&u16::from(c));
    let drive = is(1, b':') && (is(2, b'\\') || is(2, b'/'));
    let unc = is(0, b'\\') && is(1, b'\\');
    let forbidden = |unit: &u16| FORBIDDEN_PATH_CHARS.iter().any(|&c| u16::from(c) == *unit);
    units.len() >= 3 && (drive || unc) && !units.iter().any(forbidden)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::self_test::FOLDER_CASES;
    use std::collections::HashMap;
    use std::os::windows::ffi::OsStringExt;

    fn folders() -> DefaultFolders {
        DefaultFolders::new(Path::new(r"C:\Users\Ada\Pictures"), Path::new(r"C:\Users\Ada\Videos"))
    }

    /// An in-memory profile that behaves like `GetPrivateProfile{String,Int}W`.
    #[derive(Default)]
    struct MapSource {
        ints: HashMap<&'static str, u32>,
        strings: HashMap<&'static str, &'static str>,
    }

    impl ProfileSource for MapSource {
        fn string(&self, key: &str, default: &OsStr) -> OsString {
            self.strings.get(key).map_or_else(|| default.to_owned(), OsString::from)
        }
        fn int(&self, key: &str, default: u32) -> u32 {
            self.ints.get(key).copied().unwrap_or(default)
        }
    }

    #[test]
    fn default_folders_append_app_subfolders() {
        let folders = folders();
        assert_eq!(folders.screenshots, Path::new(r"C:\Users\Ada\Pictures\Mosaiks-Screenshots"));
        assert_eq!(folders.recordings, Path::new(r"C:\Users\Ada\Videos\Mosaiks-Aufnahmen"));
        // A failed known-folder lookup yields an (invalid) rooted path, as before.
        let empty = DefaultFolders::new(Path::new(""), Path::new(""));
        assert_eq!(empty.screenshots, Path::new(r"\Mosaiks-Screenshots"));
    }

    #[test]
    fn empty_profile_loads_defaults() {
        let config = Config::load(&MapSource::default(), &folders());
        assert_eq!(config, Config::defaults(&folders()));
        assert_eq!(config.screenshot_hotkey, "Shift+Alt+4");
        assert_eq!(config.recording_hotkey, "Shift+Alt+5");
        assert!(!config.microphone);
        assert!(config.system_audio && config.show_clicks && config.autostart);
        assert_eq!(config.frame_rate, FrameRate::Fps60);
        assert_eq!(config.countdown, Countdown::Seconds3);
        assert!(config.validate().is_ok());
    }

    #[test]
    fn stored_values_override_defaults() {
        let mut source = MapSource::default();
        source.strings.insert(keys::SCREENSHOT_HOTKEY, "Strg+F1");
        source.strings.insert(keys::RECORDING_FOLDER, "D:\\Videos");
        source.ints.insert(keys::MICROPHONE, 1);
        source.ints.insert(keys::SYSTEM_AUDIO, 0);
        source.ints.insert(keys::AUTOSTART, 7);
        source.ints.insert(keys::FPS, 30);
        source.ints.insert(keys::COUNTDOWN, 5);
        let config = Config::load(&source, &folders());
        assert_eq!(config.screenshot_hotkey, "Strg+F1");
        assert_eq!(config.recording_folder, Path::new(r"D:\Videos"));
        assert!(config.microphone);
        assert!(!config.system_audio);
        assert!(config.autostart, "any non-zero value is true");
        assert_eq!(config.frame_rate, FrameRate::Fps30);
        assert_eq!(config.countdown, Countdown::Seconds5);
    }

    #[test]
    fn frame_rate_normalisation() {
        assert_eq!(FrameRate::from_value(30), FrameRate::Fps30);
        for value in [0, 29, 31, 60, 120, u32::MAX] {
            assert_eq!(FrameRate::from_value(value), FrameRate::Fps60, "{value}");
        }
        for rate in [FrameRate::Fps30, FrameRate::Fps60] {
            assert_eq!(FrameRate::from_value(rate.value()), rate);
            assert_eq!(FrameRate::from_index(Some(rate.index())), rate);
        }
        assert_eq!(FrameRate::from_index(None), FrameRate::Fps60);
        assert_eq!(FrameRate::from_index(Some(5)), FrameRate::Fps60);
    }

    #[test]
    fn countdown_normalisation() {
        assert_eq!(Countdown::from_value(0), Countdown::Off);
        assert_eq!(Countdown::from_value(5), Countdown::Seconds5);
        for value in [1, 3, 4, 6, 10, u32::MAX] {
            assert_eq!(Countdown::from_value(value), Countdown::Seconds3, "{value}");
        }
        for countdown in [Countdown::Off, Countdown::Seconds3, Countdown::Seconds5] {
            assert_eq!(Countdown::from_value(countdown.value()), countdown);
            assert_eq!(Countdown::from_index(Some(countdown.index())), countdown);
        }
        assert_eq!(Countdown::from_index(None), Countdown::Seconds3);
        assert_eq!(Countdown::from_index(Some(9)), Countdown::Seconds3);
    }

    #[test]
    fn to_ini_matches_profile_api_layout() {
        let mut config = Config::defaults(&folders());
        config.frame_rate = FrameRate::Fps30;
        config.countdown = Countdown::Off;
        let expected = "\r\n[capture]\r\n\
            screenshot_hotkey=Shift+Alt+4\r\n\
            recording_hotkey=Shift+Alt+5\r\n\
            screenshot_folder=C:\\Users\\Ada\\Pictures\\Mosaiks-Screenshots\r\n\
            recording_folder=C:\\Users\\Ada\\Videos\\Mosaiks-Aufnahmen\r\n\
            microphone=0\r\n\
            system_audio=1\r\n\
            show_clicks=1\r\n\
            autostart=1\r\n\
            fps=30\r\n\
            countdown=0\r\n";
        assert_eq!(config.to_ini(), expected);
    }

    #[test]
    fn values_that_the_profile_api_would_alter_are_quoted() {
        let quote = |text: &str| quote_if_needed(OsString::from(text));
        assert_eq!(quote(r"C:\Bilder"), r"C:\Bilder", "ordinary values stay as they are");
        assert_eq!(quote(""), "");
        assert_eq!(quote(r"C:\Ordner "), r#""C:\Ordner ""#);
        assert_eq!(quote(" Alt+4"), r#"" Alt+4""#);
        assert_eq!(quote("\tx"), "\"\tx\"");
        assert_eq!(quote(r#""x""#), r#"""x"""#);
        assert_eq!(quote("'x'"), r#""'x'""#);
        assert_eq!(quote(r#""x"#), r#""x"#, "an unmatched quote is not removed on read");
        assert_eq!(quote("'"), "'");
    }

    #[test]
    fn hotkey_validation() {
        let mut config = Config::defaults(&folders());
        let hotkeys = config.hotkeys().unwrap();
        assert_eq!(hotkeys.screenshot.key, u16::from(b'4'));
        assert_eq!(hotkeys.recording.key, u16::from(b'5'));

        config.recording_hotkey = "Alt + Shift + 4".to_owned();
        assert_eq!(config.hotkeys(), Err(ConfigError::DuplicateHotkey));

        config.recording_hotkey = "Alt+".to_owned();
        assert!(matches!(config.hotkeys(), Err(ConfigError::InvalidHotkey(_))));
    }

    #[test]
    fn validation_checks_hotkeys_before_folders() {
        let mut config = Config::defaults(&folders());
        config.screenshot_folder = PathBuf::from("relative");
        assert_eq!(config.validate(), Err(ConfigError::InvalidFolder));
        config.screenshot_hotkey = "nonsense".to_owned();
        assert!(matches!(config.validate(), Err(ConfigError::InvalidHotkey(_))));
        config.screenshot_hotkey = DEFAULT_SCREENSHOT_HOTKEY.to_owned();
        config.screenshot_folder = folders().screenshots;
        config.recording_folder = PathBuf::from(r"C:\a|b");
        assert_eq!(config.validate(), Err(ConfigError::InvalidFolder));
    }

    #[test]
    fn folder_self_test_cases_hold() {
        for &(path, expected) in FOLDER_CASES {
            assert_eq!(is_valid_folder(Path::new(path)), expected, "{path:?}");
        }
    }

    #[test]
    fn folder_validation() {
        let valid = [
            "C:\\",
            "c:/",
            "D:\\Bilder",
            "Z:/Videos/Neu",
            "\\\\server\\share",
            "\\\\server",
            "C:\\Ordner mit Leerzeichen\\ä",
        ];
        for path in valid {
            assert!(is_valid_folder(Path::new(path)), "{path:?}");
        }
        let invalid = [
            "",
            "C:",
            "C:x",
            "\\\\",
            "\\Ordner",
            "/Ordner",
            "Ordner\\C:\\",
            "C:\\a\rb",
            "C:\\\"quoted\"",
            "C:\\*",
            "C:\\a<b",
            "C:\\a>b",
            "C:\\what?",
            "C:\\a|b",
        ];
        for path in invalid {
            assert!(!is_valid_folder(Path::new(path)), "{path:?}");
        }
        // Unpaired surrogates are legal in Windows file names and stay valid.
        let odd =
            OsString::from_wide(&[u16::from(b'C'), u16::from(b':'), u16::from(b'\\'), 0xD800]);
        assert!(is_valid_folder(Path::new(&odd)));
        let odd_forbidden =
            OsString::from_wide(&[u16::from(b'C'), u16::from(b':'), 0x5C, 0xD800, 0x3F]);
        assert!(!is_valid_folder(Path::new(&odd_forbidden)));
    }
}
