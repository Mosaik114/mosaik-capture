//! Test isolation (`MOSAIK_CAPTURE_TEST_INSTANCE`, `MOSAIK_CAPTURE_TEST_CONFIG`).
//!
//! An isolated host and the engine it starts append `.Test.<instance>` to every mutex,
//! window class and title, keep their data below `<temp>\MosaikCapture.Test.<instance>`
//! instead of `%LOCALAPPDATA%`, and never touch autostart. So a test (or a preview with
//! another settings file) can neither reach, block nor overwrite the real instance —
//! the derived names can never equal the real ones. The engine applies the same rules
//! (`engine/Core/TestIsolation.cs`); both are pinned by `contract/contract.json`.

use std::env;
use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};

use crate::store;

pub const INSTANCE_VAR: &str = "MOSAIK_CAPTURE_TEST_INSTANCE";
pub const CONFIG_VAR: &str = "MOSAIK_CAPTURE_TEST_CONFIG";
/// The instance name when only a settings file is given.
pub const DEFAULT_INSTANCE: &str = "preview";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Isolation {
    instance: String,
    /// Stands in for `%LOCALAPPDATA%`.
    pub local_app_data: PathBuf,
    /// The configured settings file, else the usual one below `local_app_data`.
    pub settings: PathBuf,
}

impl Isolation {
    pub fn from_env() -> Option<Self> {
        Self::from_vars(env::var_os(INSTANCE_VAR), env::var_os(CONFIG_VAR), &env::temp_dir())
    }

    fn from_vars(
        instance: Option<OsString>,
        config: Option<OsString>,
        temp: &Path,
    ) -> Option<Self> {
        let instance = instance.filter(|value| !value.is_empty());
        let config = config.filter(|value| !value.is_empty());
        if instance.is_none() && config.is_none() {
            return None;
        }
        let instance = instance.map_or_else(|| DEFAULT_INSTANCE.to_owned(), |name| sanitize(&name));
        // Absolute right away: the profile API would resolve a relative name in the
        // Windows folder, and the engine gets the same path.
        let local_app_data = temp.join(format!("MosaikCapture.Test.{instance}"));
        let settings = config.map_or_else(
            || store::settings_below(&local_app_data),
            |path| std::path::absolute(&path).unwrap_or_else(|_| path.into()),
        );
        Some(Self { instance, local_app_data, settings })
    }

    #[cfg(test)]
    pub fn for_test(instance: &str, temp: &Path) -> Self {
        Self::from_vars(Some(instance.into()), None, temp).unwrap_or_else(|| unreachable!())
    }

    /// `name` with this instance's suffix.
    pub fn name(&self, name: &str) -> String {
        format!("{name}.Test.{}", self.instance)
    }

    /// The variables that make an engine started by this host isolate itself the same
    /// way and read the same settings.
    pub fn engine_env(&self) -> [(&'static str, OsString); 2] {
        [(INSTANCE_VAR, OsString::from(&self.instance)), (CONFIG_VAR, self.settings.clone().into())]
    }
}

/// `name` as used by this process: unchanged, or with the test suffix when isolated.
pub fn isolated(name: &str, isolation: Option<&Isolation>) -> String {
    isolation.map_or_else(|| name.to_owned(), |isolation| isolation.name(name))
}

/// Keeps `A-Z a-z 0-9 _ -` and replaces every other UTF-16 unit with `_` (exactly what
/// the engine does), so the result is valid in mutex and class names.
fn sanitize(name: &OsStr) -> String {
    name.encode_wide()
        .map(|unit| match u8::try_from(unit) {
            Ok(byte) if byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-' => {
                char::from(byte)
            }
            _ => '_',
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn vars(instance: Option<&str>, config: Option<&str>) -> Option<Isolation> {
        Isolation::from_vars(
            instance.map(OsString::from),
            config.map(OsString::from),
            Path::new(r"C:\T"),
        )
    }

    #[test]
    fn no_variables_no_isolation() {
        assert_eq!(vars(None, None), None);
        assert_eq!(vars(Some(""), Some("")), None, "empty means unset");
        assert_eq!(isolated("MosaikCapture.Host", None), "MosaikCapture.Host");
    }

    #[test]
    fn an_instance_gets_its_own_names_and_folder() {
        let isolation = vars(Some("ci-42"), None).unwrap();
        assert_eq!(
            isolated("MosaikCapture.Host", Some(&isolation)),
            "MosaikCapture.Host.Test.ci-42"
        );
        assert_eq!(isolation.local_app_data, Path::new(r"C:\T\MosaikCapture.Test.ci-42"));
        assert_eq!(
            isolation.settings,
            Path::new(r"C:\T\MosaikCapture.Test.ci-42\MosaikCapture\settings.ini")
        );
    }

    #[test]
    fn a_settings_file_alone_isolates_as_preview() {
        let isolation = vars(None, Some(r"D:\qa\settings.ini")).unwrap();
        assert_eq!(isolation.name("X"), "X.Test.preview");
        assert_eq!(isolation.settings, Path::new(r"D:\qa\settings.ini"));
        let relative = vars(None, Some(r"qa\settings.ini")).unwrap().settings;
        assert!(relative.is_absolute() && relative.ends_with(r"qa\settings.ini"));
    }

    #[test]
    fn engine_gets_both_variables() {
        let isolation = vars(Some("t"), Some(r"C:\s.ini")).unwrap();
        let env = isolation.engine_env();
        assert_eq!(
            env,
            [(INSTANCE_VAR, OsString::from("t")), (CONFIG_VAR, OsString::from(r"C:\s.ini"))]
        );
    }
}
