//! Checks the host against `contract/contract.json`, the file the engine's tests check
//! the engine against. Constants are compared with the file, never with literals here.

use std::collections::BTreeMap;
use std::ffi::OsStr;
use std::path::Path;

use serde_json::Value;
use windows_sys::Win32::UI::WindowsAndMessaging::WM_APP;

use crate::config::{self, Config, Countdown, DefaultFolders, FrameRate, MAX_FOLDER_LEN, SECTION};
use crate::engine::{self, CaptureMode};
use crate::ipc::{self, AudioSettings, ProbeResponse};
use crate::isolation::{self, Isolation};
use crate::store::tests::{TempDir, write_utf16};
use crate::store::{self, IniFile};
use crate::{Exit, single_instance};

pub struct Contract(Value);

pub fn load() -> Contract {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("contract").join("contract.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("{}: {error}", path.display()));
    Contract(serde_json::from_str(&text).expect("contract.json is valid JSON"))
}

impl Contract {
    pub fn get(&self, path: &[&str]) -> &Value {
        path.iter().fold(&self.0, |value, key| &value[key])
    }

    pub fn str(&self, path: &[&str]) -> &str {
        self.get(path).as_str().unwrap_or_else(|| panic!("{path:?} is not a string"))
    }

    pub fn int(&self, path: &[&str]) -> i64 {
        self.get(path).as_i64().unwrap_or_else(|| panic!("{path:?} is not an integer"))
    }

    /// The entries of an object, without `$comment`.
    fn entries(&self, path: &[&str]) -> BTreeMap<&str, &Value> {
        let object =
            self.get(path).as_object().unwrap_or_else(|| panic!("{path:?} is not an object"));
        object
            .iter()
            .filter(|(key, _)| !key.starts_with('$'))
            .map(|(k, v)| (k.as_str(), v))
            .collect()
    }

    fn message(&self, path: &[&str]) -> u32 {
        WM_APP + u32::try_from(self.int(path)).unwrap()
    }
}

/// `key=value` lines of the section the host writes.
fn ini_entries(ini: &OsStr) -> BTreeMap<String, String> {
    let text = ini.to_string_lossy();
    text.lines()
        .filter_map(|line| line.split_once('='))
        .map(|(key, value)| (key.to_owned(), value.to_owned()))
        .collect()
}

fn folders() -> DefaultFolders {
    DefaultFolders::new(Path::new(r"C:\P"), Path::new(r"C:\V"))
}

#[test]
fn settings_file_location_and_layout() {
    let c = load();
    assert_eq!(SECTION, c.str(&["settings", "section"]));
    assert_eq!(store::APP_FOLDER, c.str(&["settings", "app_folder"]));
    assert_eq!(store::LEGACY_APP_FOLDER, c.str(&["settings", "legacy_app_folder"]));
    assert_eq!(store::FILE_NAME, c.str(&["settings", "file_name"]));
    assert_eq!(i64::try_from(MAX_FOLDER_LEN).unwrap(), c.int(&["settings", "max_folder_length"]));
    assert!(
        Config::defaults(&folders()).to_ini().to_string_lossy().contains(&format!("[{SECTION}]"))
    );
}

#[test]
fn the_host_writes_exactly_the_contract_keys_with_their_defaults() {
    let c = load();
    let keys = c.entries(&["settings", "keys"]);
    let written = ini_entries(&Config::defaults(&folders()).to_ini());
    assert_eq!(
        written.keys().map(String::as_str).collect::<Vec<_>>(),
        keys.keys().copied().collect::<Vec<_>>()
    );
    for (key, spec) in keys {
        if spec["kind"] == "folder" {
            let name = c.str(&["settings", "default_folders", key, "name"]);
            assert!(written[key].ends_with(&format!(r"\{name}")), "{key}: {}", written[key]);
        } else {
            let default = &spec["default"];
            let expected = default.as_str().map_or_else(|| default.to_string(), str::to_owned);
            assert_eq!(written[key], expected, "default of {key}");
        }
    }
    let defaults = c.entries(&["settings", "default_folders"]);
    assert_eq!(defaults["screenshot_folder"]["name"], config::SCREENSHOT_FOLDER_NAME);
    assert_eq!(defaults["recording_folder"]["name"], config::RECORDING_FOLDER_NAME);
}

/// The normalisation of a stored number for one key, as the contract describes it.
fn expected(spec: &Value, stored: Option<i64>) -> i64 {
    let value = stored.unwrap_or_else(|| spec["default"].as_i64().unwrap());
    match spec["kind"].as_str() {
        Some("flag") => i64::from(value != 0),
        Some("choice") => {
            let allowed = spec["allowed"].as_array().unwrap();
            if allowed.iter().any(|a| a.as_i64() == Some(value)) {
                value
            } else {
                spec["otherwise"].as_i64().unwrap()
            }
        }
        kind => panic!("not a number kind: {kind:?}"),
    }
}

fn loaded_numbers(config: &Config) -> BTreeMap<&'static str, i64> {
    BTreeMap::from([
        ("microphone", i64::from(config.microphone)),
        ("system_audio", i64::from(config.system_audio)),
        ("show_clicks", i64::from(config.show_clicks)),
        ("autostart", i64::from(config.autostart)),
        ("fps", i64::from(config.frame_rate.value())),
        ("countdown", i64::from(config.countdown.value())),
    ])
}

#[test]
fn stored_numbers_are_read_and_normalised_as_the_contract_says() {
    let c = load();
    let keys = c.entries(&["settings", "keys"]);
    let dir = TempDir::new();
    let path = dir.file("settings.ini");
    for case in c.get(&["settings", "numbers", "cases"]).as_array().unwrap() {
        let raw = case[0].as_str().unwrap();
        let stored = case[1].as_i64();
        let numeric: Vec<&str> =
            loaded_numbers(&Config::defaults(&folders())).keys().copied().collect();
        let lines: String = numeric.iter().map(|key| [key, "=", raw, "\r\n"].concat()).collect();
        write_utf16(&path, &format!("[capture]\r\n{lines}"));
        let loaded = loaded_numbers(&Config::load(&IniFile::new(&path), &folders()));
        for key in numeric {
            assert_eq!(loaded[key], expected(keys[key], stored), "{key}={raw:?}");
        }
    }
}

#[test]
fn choices_cover_the_whole_range() {
    let c = load();
    let keys = c.entries(&["settings", "keys"]);
    for value in -2..=130 {
        let stored = Some(i64::from(value));
        assert_eq!(i64::from(FrameRate::from_value(value).value()), expected(keys["fps"], stored));
        assert_eq!(
            i64::from(Countdown::from_value(value).value()),
            expected(keys["countdown"], stored)
        );
    }
}

#[test]
fn host_window_messages_and_replies() {
    let c = load();
    let host = |key: &str| c.message(&["host", "messages", key]);
    assert_eq!(single_instance::MUTEX_NAME, c.str(&["host", "mutex"]));
    assert_eq!(ipc::WINDOW_CLASS, c.str(&["host", "window_class"]));
    let ours = [
        ("show_settings", ipc::SHOW_SETTINGS),
        ("show_recording_settings", ipc::SHOW_RECORDING_SETTINGS),
        ("take_screenshot", ipc::TAKE_SCREENSHOT),
        ("toggle_recording", ipc::TOGGLE_RECORDING),
        ("quit", ipc::QUIT),
        ("probe", ipc::PROBE),
        ("audio_settings", ipc::AUDIO_SETTINGS),
    ];
    assert_eq!(ours.len(), c.entries(&["host", "messages"]).len());
    for (name, message) in ours {
        assert_eq!(message, host(name), "{name}");
        assert!(ipc::needs_startup(message), "{name} waits for start-up");
    }
    assert!(
        ours.iter().all(|&(_, message)| message != ipc::TRAY_CALLBACK),
        "internal message is free"
    );

    let reply = |key: &str| isize::try_from(c.int(&["host", "quit_replies", key])).unwrap();
    assert_eq!(ipc::QUIT_ACCEPTED, reply("accepted"));
    assert_eq!(ipc::QUIT_REFUSED, reply("refused"));

    let bit = |key: &str| isize::try_from(c.int(&["host", "probe_bits", key])).unwrap();
    assert_eq!(ProbeResponse::default().to_lresult(), bit("alive"));
    let screenshot = ProbeResponse { screenshot_hotkey: true, recording_hotkey: false };
    assert_eq!(screenshot.to_lresult(), bit("alive") | bit("screenshot_hotkey"));
    let recording = ProbeResponse { screenshot_hotkey: false, recording_hotkey: true };
    assert_eq!(recording.to_lresult(), bit("alive") | bit("recording_hotkey"));
    assert_eq!(
        ProbeResponse::HEALTHY,
        bit("alive") | bit("screenshot_hotkey") | bit("recording_hotkey")
    );

    let audio = |key: &str| usize::try_from(c.int(&["host", "audio_bits", key])).unwrap();
    let only = AudioSettings::from_wparam;
    assert_eq!(
        only(audio("microphone")),
        AudioSettings { microphone: true, system_audio: false, show_clicks: false }
    );
    assert_eq!(
        only(audio("system_audio")),
        AudioSettings { microphone: false, system_audio: true, show_clicks: false }
    );
    assert_eq!(
        only(audio("show_clicks")),
        AudioSettings { microphone: false, system_audio: false, show_clicks: true }
    );
}

#[test]
fn exit_codes() {
    let c = load();
    let ours = [
        ("success", Exit::Success),
        ("failed", Exit::Failed),
        ("self_test_failed", Exit::SelfTestFailed),
        ("probe_failed", Exit::ProbeFailed),
        ("quit_refused", Exit::QuitRefused),
    ];
    assert_eq!(ours.len(), c.entries(&["host", "exit_codes"]).len());
    for (name, exit) in ours {
        assert_eq!(i64::from(exit as u8), c.int(&["host", "exit_codes", name]), "{name}");
    }
}

#[test]
fn engine_names_messages_and_state_bits() {
    let c = load();
    assert_eq!(engine::EXE_NAME, c.str(&["engine", "executable"]));
    assert_eq!(engine::WINDOW_TITLE, c.str(&["engine", "window_title"]));
    assert_eq!(engine::MSG_SCREENSHOT, c.message(&["engine", "messages", "screenshot"]));
    assert_eq!(engine::MSG_RECORDING, c.message(&["engine", "messages", "recording"]));
    assert_eq!(engine::MSG_QUIT, c.message(&["engine", "messages", "quit"]));
    assert_eq!(engine::MSG_QUERY_STATE, c.message(&["engine", "messages", "query_state"]));
    assert_eq!(
        engine::STATE_BUSY,
        usize::try_from(c.int(&["engine", "state_bits", "busy"])).unwrap()
    );
    assert_eq!(CaptureMode::Screenshot.flag(), c.str(&["engine", "arguments", "screenshot"]));
    assert_eq!(CaptureMode::Recording.flag(), c.str(&["engine", "arguments", "recording"]));
}

#[test]
fn test_isolation_names() {
    let c = load();
    let t = |key: &str| c.str(&["test_isolation", key]);
    assert_eq!(isolation::INSTANCE_VAR, t("instance_variable"));
    assert_eq!(isolation::CONFIG_VAR, t("config_variable"));
    assert_eq!(isolation::DEFAULT_INSTANCE, t("default_instance"));
    let temp = Path::new(r"C:\Temp");
    let isolation = Isolation::for_test("x", temp);
    assert_eq!(isolation.name("N"), format!("N{}x", t("infix")));
    assert_eq!(isolation.local_app_data, temp.join(format!("{}x", t("local_app_data_prefix"))));
    for example in c.get(&["test_isolation", "examples"]).as_array().unwrap() {
        let isolation = Isolation::for_test(example["instance"].as_str().unwrap(), temp);
        assert_eq!(
            isolation.name(c.str(&["engine", "window_title"])),
            example["engine_window_title"]
        );
        assert_eq!(isolation.name(c.str(&["host", "window_class"])), example["host_window_class"]);
        assert_eq!(isolation.name(c.str(&["engine", "mutex"])), example["engine_mutex"]);
    }
}
