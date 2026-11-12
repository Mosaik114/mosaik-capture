//! Conversions between Rust strings and the NUL-terminated UTF-16 strings Win32 expects.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;

/// Encodes `text` as UTF-16 with a trailing NUL, ready to pass as `PCWSTR`.
pub fn to_wide(text: impl AsRef<OsStr>) -> Vec<u16> {
    text.as_ref().encode_wide().chain(Some(0)).collect()
}

/// Decodes UTF-16 up to the first NUL (or the end of `units`).
///
/// Unpaired surrogates are replaced with U+FFFD.
pub fn from_wide(units: &[u16]) -> String {
    let end = units.iter().position(|&unit| unit == 0).unwrap_or(units.len());
    String::from_utf16_lossy(&units[..end])
}

/// Decodes a NUL-terminated UTF-16 string returned by an API (e.g. a `PWSTR`).
/// A null pointer yields an empty string.
///
/// # Safety
///
/// `ptr` must be null or point to a readable, NUL-terminated UTF-16 string.
pub unsafe fn from_wide_ptr(ptr: *const u16) -> String {
    if ptr.is_null() {
        return String::new();
    }
    let mut len = 0;
    // SAFETY: the caller guarantees a terminator, so every index up to it is readable.
    while unsafe { *ptr.add(len) } != 0 {
        len += 1;
    }
    // SAFETY: the `len` units before the terminator were just read.
    String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(ptr, len) })
}

/// Copies `text` into a fixed-size buffer such as `NOTIFYICONDATAW::szTip`,
/// truncating if necessary and always leaving room for the terminating NUL.
pub fn copy_to_buffer(buffer: &mut [u16], text: &str) {
    let Some(capacity) = buffer.len().checked_sub(1) else {
        return;
    };
    let mut written = 0;
    for (slot, unit) in buffer.iter_mut().zip(text.encode_utf16().take(capacity)) {
        *slot = unit;
        written += 1;
    }
    buffer[written] = 0;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn to_wide_appends_terminator() {
        assert_eq!(to_wide("Ab"), [u16::from(b'A'), u16::from(b'b'), 0]);
        assert_eq!(to_wide(""), [0]);
    }

    #[test]
    fn round_trip_keeps_non_ascii_text() {
        let text = "Grüße – 📷";
        assert_eq!(from_wide(&to_wide(text)), text);
    }

    #[test]
    fn from_wide_stops_at_first_nul() {
        let units = [u16::from(b'a'), 0, u16::from(b'b')];
        assert_eq!(from_wide(&units), "a");
        assert_eq!(from_wide(&[u16::from(b'x')]), "x");
    }

    #[test]
    fn from_wide_ptr_reads_up_to_terminator() {
        let text = to_wide("Grüße");
        // SAFETY: `text` is NUL-terminated.
        assert_eq!(unsafe { from_wide_ptr(text.as_ptr()) }, "Grüße");
        // SAFETY: null is explicitly allowed.
        assert_eq!(unsafe { from_wide_ptr(std::ptr::null()) }, "");
    }

    #[test]
    fn copy_to_buffer_truncates_and_terminates() {
        let mut buffer = [0xFFFF; 4];
        copy_to_buffer(&mut buffer, "abcdef");
        assert_eq!(from_wide(&buffer), "abc");
        assert_eq!(buffer[3], 0);

        let mut buffer = [0xFFFF; 8];
        copy_to_buffer(&mut buffer, "ok");
        assert_eq!(from_wide(&buffer), "ok");

        copy_to_buffer(&mut [], "ignored");
    }
}
