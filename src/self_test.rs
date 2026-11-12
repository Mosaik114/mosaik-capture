//! The built-in `--self-test`: a quick check of the pure parsing and validation logic
//! that runs inside the shipped executable (used by the release build script).
//!
//! The same tables are also exercised by the unit tests.

use crate::config::is_valid_folder;
use crate::hotkey::Hotkey;

/// Hotkey strings with the expected `(modifier bits, virtual-key code)`, or `None` if
/// the text must be rejected.
pub const HOTKEY_CASES: &[(&str, Option<(u32, u16)>)] = &[
    ("Shift+Alt+4", Some((5, 52))),
    ("Alt + Umschalt + 5", Some((5, 53))),
    ("Strg+Shift+F12", Some((6, 123))),
    ("ctrl+space", Some((2, 32))),
    ("Shift+Alt+F24", Some((5, 135))),
    ("Alt+F25", None),
    ("Shift+Shift+4", None),
    ("Alt+4+5", None),
    ("4", None),
    ("Shift+Alt", None),
    ("Alt++4", None),
    ("Alt+F0", None),
];

/// Folder paths with the expected validation result.
pub const FOLDER_CASES: &[(&str, bool)] =
    &[("C:\\Bilder\\Grüße", true), ("relativ\\Bilder", false), ("C:\\Bilder\nneu", false)];

/// Runs all checks and reports whether every one passed.
pub fn run() -> bool {
    let hotkeys_ok = HOTKEY_CASES.iter().all(|&(text, expected)| {
        let parsed = text.parse::<Hotkey>().ok();
        parsed.map(|key| (key.modifiers.bits(), key.key)) == expected
    });
    let folders_ok = FOLDER_CASES.iter().all(|&(path, expected)| is_valid_folder(path) == expected);
    hotkeys_ok && folders_ok
}

#[cfg(test)]
mod tests {
    #[test]
    fn self_test_passes() {
        assert!(super::run());
    }
}
