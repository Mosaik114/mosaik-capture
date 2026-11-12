//! Parsing of user-entered hotkeys such as `Shift+Alt+4` or `Strg + Leertaste`.
//!
//! The format is a `+`-separated list of tokens. Spaces are ignored everywhere and
//! tokens are matched case-insensitively. A hotkey needs at least one modifier and
//! exactly one key:
//!
//! * modifiers: `Shift`/`Umschalt`, `Alt`, `Ctrl`/`Control`/`Strg`
//! * keys: a single letter `A`–`Z` or digit `0`–`9`, `Space`/`Leertaste`, `F1`–`F24`

use std::fmt;
use std::ops::{BitOr, BitOrAssign};
use std::str::FromStr;

use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    MOD_ALT, MOD_CONTROL, MOD_SHIFT, VK_F1, VK_SPACE,
};

/// Tokens longer than this are rejected outright.
const MAX_TOKEN_LEN: usize = 24;
/// Highest supported function key (`F24`).
const MAX_FUNCTION_KEY: u16 = 24;

/// A set of modifier keys, using the `MOD_*` bit values `RegisterHotKey` expects.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct Modifiers(u32);

impl Modifiers {
    /// No modifier.
    pub const NONE: Self = Self(0);
    /// The Alt key.
    pub const ALT: Self = Self(MOD_ALT);
    /// The Ctrl key.
    pub const CONTROL: Self = Self(MOD_CONTROL);
    /// The Shift key.
    pub const SHIFT: Self = Self(MOD_SHIFT);

    /// The raw `MOD_*` bits.
    pub const fn bits(self) -> u32 {
        self.0
    }

    /// Whether every modifier in `other` is also set in `self`.
    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }

    /// Whether no modifier is set.
    pub const fn is_empty(self) -> bool {
        self.0 == 0
    }

    /// Recognises a modifier token (German and English spellings).
    fn from_token(token: &str) -> Option<Self> {
        let is = |word: &str| token.eq_ignore_ascii_case(word);
        if is("SHIFT") || is("UMSCHALT") {
            Some(Self::SHIFT)
        } else if is("ALT") {
            Some(Self::ALT)
        } else if is("CTRL") || is("CONTROL") || is("STRG") {
            Some(Self::CONTROL)
        } else {
            None
        }
    }
}

impl BitOr for Modifiers {
    type Output = Self;

    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

impl BitOrAssign for Modifiers {
    fn bitor_assign(&mut self, rhs: Self) {
        self.0 |= rhs.0;
    }
}

/// A global hotkey: modifiers plus one virtual-key code.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Hotkey {
    /// Modifier keys that must be held.
    pub modifiers: Modifiers,
    /// The virtual-key code (`VK_*`).
    pub key: u16,
}

/// Why a hotkey string was rejected.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ParseHotkeyError {
    /// The text is empty or contains an empty token, as in `Alt++4`.
    EmptyToken,
    /// A token is longer than any valid key name.
    TokenTooLong,
    /// The same modifier appears twice.
    DuplicateModifier,
    /// More than one non-modifier key was given.
    MultipleKeys,
    /// A token is neither a modifier nor a supported key.
    UnknownKey,
    /// Only modifiers were given.
    MissingKey,
    /// A key was given without any modifier.
    MissingModifier,
}

impl fmt::Display for ParseHotkeyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::EmptyToken => "empty key name",
            Self::TokenTooLong => "key name is too long",
            Self::DuplicateModifier => "modifier given twice",
            Self::MultipleKeys => "more than one key given",
            Self::UnknownKey => "unknown key",
            Self::MissingKey => "no key given",
            Self::MissingModifier => "no modifier given",
        })
    }
}

impl std::error::Error for ParseHotkeyError {}

impl FromStr for Hotkey {
    type Err = ParseHotkeyError;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        let compact: String = text.chars().filter(|&c| c != ' ').collect();
        let mut modifiers = Modifiers::NONE;
        let mut key = None;

        for token in compact.split('+') {
            if token.is_empty() {
                return Err(ParseHotkeyError::EmptyToken);
            }
            if token.chars().count() > MAX_TOKEN_LEN {
                return Err(ParseHotkeyError::TokenTooLong);
            }
            if let Some(modifier) = Modifiers::from_token(token) {
                if modifiers.contains(modifier) {
                    return Err(ParseHotkeyError::DuplicateModifier);
                }
                modifiers |= modifier;
            } else if key.is_some() {
                return Err(ParseHotkeyError::MultipleKeys);
            } else {
                key = Some(parse_key(token).ok_or(ParseHotkeyError::UnknownKey)?);
            }
        }

        let key = key.ok_or(ParseHotkeyError::MissingKey)?;
        if modifiers.is_empty() {
            return Err(ParseHotkeyError::MissingModifier);
        }
        Ok(Self { modifiers, key })
    }
}

/// Maps a key token to its virtual-key code.
fn parse_key(token: &str) -> Option<u16> {
    let mut chars = token.chars();
    if let (Some(c), None) = (chars.next(), chars.next()) {
        // Letters and digits share their virtual-key codes with uppercase ASCII.
        return u8::try_from(c)
            .ok()
            .filter(u8::is_ascii_alphanumeric)
            .map(|b| u16::from(b.to_ascii_uppercase()));
    }
    if token.eq_ignore_ascii_case("SPACE") || token.eq_ignore_ascii_case("LEERTASTE") {
        return Some(VK_SPACE);
    }
    let number = token.strip_prefix(['F', 'f'])?;
    if number.is_empty() || !number.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    // Leading zeros are accepted (`F07`); overflow means "far too large".
    let n: u16 = number.parse().ok()?;
    (1..=MAX_FUNCTION_KEY).contains(&n).then(|| VK_F1 + n - 1)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::self_test::HOTKEY_CASES;

    fn key(modifiers: u32, key: u16) -> Hotkey {
        Hotkey {
            modifiers: Modifiers(modifiers),
            key,
        }
    }

    #[test]
    fn self_test_cases_hold() {
        for &(text, expected) in HOTKEY_CASES {
            let parsed = text
                .parse::<Hotkey>()
                .ok()
                .map(|k| (k.modifiers.bits(), k.key));
            assert_eq!(parsed, expected, "{text:?}");
        }
    }

    #[test]
    fn letters_and_digits_are_case_insensitive() {
        assert_eq!("alt+a".parse(), Ok(key(MOD_ALT, u16::from(b'A'))));
        assert_eq!("ALT+Z".parse(), Ok(key(MOD_ALT, u16::from(b'Z'))));
        assert_eq!("sHiFt+0".parse(), Ok(key(MOD_SHIFT, u16::from(b'0'))));
        assert_eq!("Strg+9".parse(), Ok(key(MOD_CONTROL, u16::from(b'9'))));
    }

    #[test]
    fn all_modifier_spellings() {
        for text in ["Shift+X", "Umschalt+X", "SHIFT+x", "umschalt+x"] {
            assert_eq!(
                text.parse::<Hotkey>().map(|k| k.modifiers),
                Ok(Modifiers::SHIFT)
            );
        }
        for text in ["Ctrl+X", "Control+X", "Strg+X", "STRG+x"] {
            assert_eq!(
                text.parse::<Hotkey>().map(|k| k.modifiers),
                Ok(Modifiers::CONTROL)
            );
        }
        let all = "Strg+Alt+Umschalt+K".parse::<Hotkey>().unwrap();
        assert_eq!(
            all.modifiers,
            Modifiers::ALT | Modifiers::CONTROL | Modifiers::SHIFT
        );
    }

    #[test]
    fn modifier_order_does_not_matter() {
        let a: Hotkey = "Shift+Alt+4".parse().unwrap();
        let b: Hotkey = "Alt+Shift+4".parse().unwrap();
        let c: Hotkey = "4+Alt+Shift".parse().unwrap();
        assert_eq!(a, b);
        assert_eq!(a, c);
    }

    #[test]
    fn space_key_spellings() {
        assert_eq!("Alt+Space".parse(), Ok(key(MOD_ALT, VK_SPACE)));
        assert_eq!("Alt+Leertaste".parse(), Ok(key(MOD_ALT, VK_SPACE)));
        assert_eq!("alt+leertaste".parse(), Ok(key(MOD_ALT, VK_SPACE)));
    }

    #[test]
    fn function_keys() {
        assert_eq!("Alt+F1".parse(), Ok(key(MOD_ALT, VK_F1)));
        assert_eq!("Alt+f12".parse(), Ok(key(MOD_ALT, VK_F1 + 11)));
        assert_eq!("Alt+F07".parse(), Ok(key(MOD_ALT, VK_F1 + 6)));
        assert_eq!("Alt+F".parse(), Ok(key(MOD_ALT, u16::from(b'F'))));
        for text in [
            "Alt+F0",
            "Alt+F25",
            "Alt+F1x",
            "Alt+Fx",
            "Alt+F-1",
            "Alt+F99999999999",
        ] {
            assert_eq!(
                text.parse::<Hotkey>(),
                Err(ParseHotkeyError::UnknownKey),
                "{text:?}"
            );
        }
    }

    #[test]
    fn spaces_are_ignored_everywhere() {
        assert_eq!(
            " Sh ift + A lt + 4 ".parse(),
            Ok(key(MOD_SHIFT | MOD_ALT, u16::from(b'4')))
        );
        assert_eq!("Ctrl + Leer taste".parse(), Ok(key(MOD_CONTROL, VK_SPACE)));
    }

    #[test]
    fn tabs_are_not_whitespace() {
        assert_eq!(
            "Alt+\t4".parse::<Hotkey>(),
            Err(ParseHotkeyError::UnknownKey)
        );
    }

    #[test]
    fn structural_errors() {
        use ParseHotkeyError as E;
        let cases = [
            ("", E::EmptyToken),
            ("   ", E::EmptyToken),
            ("+Alt+4", E::EmptyToken),
            ("Alt+4+", E::EmptyToken),
            ("Alt++4", E::EmptyToken),
            ("Alt+Alt+4", E::DuplicateModifier),
            ("Strg+Ctrl+4", E::DuplicateModifier),
            ("Alt+4+5", E::MultipleKeys),
            ("Alt+Space+F1", E::MultipleKeys),
            ("Alt+Shift", E::MissingKey),
            ("Alt", E::MissingKey),
            ("A", E::MissingModifier),
            ("Space", E::MissingModifier),
            ("Alt+Ä", E::UnknownKey),
            ("Alt+Enter", E::UnknownKey),
            ("Alt+-", E::UnknownKey),
            ("Win+4", E::UnknownKey),
        ];
        for (text, error) in cases {
            assert_eq!(text.parse::<Hotkey>(), Err(error), "{text:?}");
        }
    }

    #[test]
    fn token_length_limit() {
        let longest = format!("Alt+F{}", "0".repeat(MAX_TOKEN_LEN - 2) + "1");
        assert_eq!(longest.parse(), Ok(key(MOD_ALT, VK_F1)));
        let too_long = format!("Alt+F{}", "0".repeat(MAX_TOKEN_LEN - 1) + "1");
        assert_eq!(
            too_long.parse::<Hotkey>(),
            Err(ParseHotkeyError::TokenTooLong)
        );
    }

    #[test]
    fn modifier_helpers() {
        assert!(Modifiers::NONE.is_empty());
        let both = Modifiers::ALT | Modifiers::SHIFT;
        assert!(both.contains(Modifiers::ALT));
        assert!(!both.contains(Modifiers::CONTROL));
        assert_eq!(both.bits(), 5);
    }

    #[test]
    fn errors_have_messages() {
        assert_eq!(ParseHotkeyError::MissingKey.to_string(), "no key given");
    }
}
