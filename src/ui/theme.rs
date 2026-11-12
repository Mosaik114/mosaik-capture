//! The dark theme: colours, fonts, brushes and the owner-drawn rounded buttons.

use windows_sys::Win32::Foundation::{COLORREF, RECT};
use windows_sys::Win32::Graphics::Gdi::{
    CLEARTYPE_QUALITY, CLIP_DEFAULT_PRECIS, CreateFontW, CreatePen, CreateSolidBrush,
    DEFAULT_CHARSET, DT_CENTER, DT_SINGLELINE, DT_VCENTER, DeleteObject, DrawFocusRect, DrawTextW,
    FW_NORMAL, FW_SEMIBOLD, HBRUSH, HDC, HFONT, HGDIOBJ, OUT_DEFAULT_PRECIS, PS_SOLID, RoundRect,
    SelectObject, SetBkColor, SetBkMode, SetTextColor, TRANSPARENT,
};
use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, ODS_FOCUS, ODS_NOFOCUSRECT, ODS_SELECTED};

use super::controls;
use super::layout::{self, BUTTON_RADIUS, Dpi, FOCUS_INSET, FontRole};
use crate::wide::to_wide;

/// Builds a `COLORREF` (which stores blue in the high byte).
pub const fn rgb(red: u8, green: u8, blue: u8) -> COLORREF {
    red as u32 | (green as u32) << 8 | (blue as u32) << 16
}

/// Window background.
pub const BACKGROUND: COLORREF = rgb(0x26, 0x26, 0x24);
/// Background of edits and secondary buttons.
pub const FIELD: COLORREF = rgb(0x35, 0x34, 0x2F);
/// Secondary button while pressed.
pub const FIELD_PRESSED: COLORREF = rgb(0x2A, 0x29, 0x25);
/// Regular text.
pub const INK: COLORREF = rgb(0xFA, 0xF9, 0xF5);
/// Primary buttons.
pub const ACCENT: COLORREF = rgb(0xD9, 0x77, 0x57);
/// Primary button while pressed.
pub const ACCENT_PRESSED: COLORREF = rgb(0xB8, 0x5F, 0x42);
/// Text on primary buttons.
pub const ON_ACCENT: COLORREF = rgb(0x14, 0x14, 0x13);
/// Outline of secondary buttons.
pub const BORDER: COLORREF = rgb(0x47, 0x47, 0x45);
/// Status text for errors.
pub const ERROR: COLORREF = rgb(0xF0, 0x90, 0x80);

const FONT_FACE: &str = "Segoe UI";

/// An owned GDI object (font, brush, pen), deleted on drop.
#[derive(Debug)]
pub struct GdiObject(HGDIOBJ);

impl GdiObject {
    /// The raw handle; valid as long as `self` lives.
    pub fn handle(&self) -> HGDIOBJ {
        self.0
    }
}

impl Drop for GdiObject {
    fn drop(&mut self) {
        // SAFETY: the object was created by this module, is owned exclusively and is
        // no longer selected into a device context when dropped.
        unsafe { DeleteObject(self.0) };
    }
}

/// Creates a solid brush.
fn solid_brush(color: COLORREF) -> GdiObject {
    // SAFETY: plain GDI object creation without pointer arguments.
    GdiObject(unsafe { CreateSolidBrush(color) })
}

fn font(dpi: Dpi, size: i32, weight: u32) -> GdiObject {
    let face = to_wide(FONT_FACE);
    // SAFETY: `face` is NUL-terminated and outlives the call.
    let font = unsafe {
        CreateFontW(
            -dpi.scale(size),
            0,
            0,
            0,
            weight.cast_signed(),
            0,
            0,
            0,
            u32::from(DEFAULT_CHARSET),
            u32::from(OUT_DEFAULT_PRECIS),
            u32::from(CLIP_DEFAULT_PRECIS),
            u32::from(CLEARTYPE_QUALITY),
            0,
            face.as_ptr(),
        )
    };
    GdiObject(font)
}

/// Brushes shared by the whole window; independent of DPI.
#[derive(Debug)]
pub struct Brushes {
    background: GdiObject,
    field: GdiObject,
}

impl Brushes {
    /// Creates the brushes.
    pub fn new() -> Self {
        Self { background: solid_brush(BACKGROUND), field: solid_brush(FIELD) }
    }

    /// Brush of the window background.
    pub fn background(&self) -> HBRUSH {
        self.background.handle()
    }

    /// Brush of edit fields.
    pub fn field(&self) -> HBRUSH {
        self.field.handle()
    }
}

/// The fonts of one DPI; rebuilt when the window moves to a monitor with another DPI.
#[derive(Debug)]
pub struct Fonts {
    body: GdiObject,
    title: GdiObject,
    heading: GdiObject,
}

impl Fonts {
    /// Creates all fonts for `dpi`.
    pub fn new(dpi: Dpi) -> Self {
        Self {
            body: font(dpi, 15, FW_NORMAL),
            title: font(dpi, 27, FW_SEMIBOLD),
            heading: font(dpi, 19, FW_SEMIBOLD),
        }
    }

    /// The font for a text role.
    pub fn get(&self, role: FontRole) -> HFONT {
        match role {
            FontRole::Body => self.body.handle(),
            FontRole::Title => self.title.handle(),
            FontRole::Heading => self.heading.handle(),
        }
    }
}

/// Colours of an owner-drawn button.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ButtonStyle {
    /// Background.
    pub fill: COLORREF,
    /// Outline.
    pub outline: COLORREF,
    /// Label.
    pub text: COLORREF,
}

impl ButtonStyle {
    /// Primary (accent) or secondary button, normal or pressed.
    pub const fn new(accent: bool, pressed: bool) -> Self {
        match (accent, pressed) {
            (true, false) => Self { fill: ACCENT, outline: ACCENT, text: ON_ACCENT },
            (true, true) => Self { fill: ACCENT_PRESSED, outline: ACCENT_PRESSED, text: ON_ACCENT },
            (false, false) => Self { fill: FIELD, outline: BORDER, text: INK },
            (false, true) => Self { fill: FIELD_PRESSED, outline: BORDER, text: INK },
        }
    }
}

/// Paints an owner-drawn button: rounded rectangle, centred label, darker while
/// pressed and with a focus rectangle when it has the keyboard focus.
pub fn draw_button(item: &DRAWITEMSTRUCT, font: HFONT, dpi: Dpi) {
    let accent = layout::is_accent_button(item.CtlID.cast_signed());
    let pressed = item.itemState & ODS_SELECTED != 0;
    let focused = item.itemState & ODS_FOCUS != 0 && item.itemState & ODS_NOFOCUSRECT == 0;
    let style = ButtonStyle::new(accent, pressed);
    let dc = item.hDC;
    let rect = item.rcItem;

    let brush = solid_brush(style.fill);
    // SAFETY: plain GDI object creation without pointer arguments.
    let pen = GdiObject(unsafe { CreatePen(PS_SOLID, dpi.scale(1).max(1), style.outline) });
    let radius = dpi.scale(BUTTON_RADIUS);
    {
        let _brush = Selected::new(dc, brush.handle());
        let _pen = Selected::new(dc, pen.handle());
        // SAFETY: `dc` is the device context Windows passed for this paint.
        unsafe { RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, radius, radius) };
    }

    let text = to_wide(controls::text(item.hwndItem));
    let _font = Selected::new(dc, font);
    let mut text_rect = rect;
    // SAFETY: `dc` is valid for this paint; `text` is NUL-terminated (length -1)
    // and `text_rect` outlives the call.
    unsafe {
        SetBkMode(dc, TRANSPARENT.cast_signed());
        SetTextColor(dc, style.text);
        DrawTextW(
            dc,
            text.as_ptr(),
            -1,
            &raw mut text_rect,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE,
        );
    }

    if focused {
        let inset = dpi.scale(FOCUS_INSET);
        let focus = RECT {
            left: rect.left + inset,
            top: rect.top + inset,
            right: rect.right - inset,
            bottom: rect.bottom - inset,
        };
        // SAFETY: `dc` is valid for this paint; `focus` outlives the call. The dotted
        // rectangle is drawn with the text colour on the fill colour.
        unsafe {
            SetBkColor(dc, style.fill);
            DrawFocusRect(dc, &raw const focus);
        }
    }
}

/// Selects a GDI object into a device context and restores the previous one on drop.
struct Selected {
    dc: HDC,
    previous: HGDIOBJ,
}

impl Selected {
    fn new(dc: HDC, object: HGDIOBJ) -> Self {
        // SAFETY: `dc` and `object` are valid for the duration of the paint, and the
        // guard is dropped before `object`.
        let previous = unsafe { SelectObject(dc, object) };
        Self { dc, previous }
    }
}

impl Drop for Selected {
    fn drop(&mut self) {
        // SAFETY: restores the object that was selected before `new`.
        unsafe { SelectObject(self.dc, self.previous) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn colours_match_original_colorref_values() {
        assert_eq!(BACKGROUND, 0x0024_2626);
        assert_eq!(FIELD, 0x002F_3435);
        assert_eq!(INK, 0x00F5_F9FA);
        assert_eq!(ACCENT, 0x0057_77D9);
        assert_eq!(ON_ACCENT, 0x0013_1414);
        assert_eq!(BORDER, 0x0045_4747);
        assert_eq!(ERROR, 0x0080_90F0);
    }

    #[test]
    fn button_styles() {
        let primary = ButtonStyle::new(true, false);
        assert_eq!((primary.fill, primary.text), (ACCENT, ON_ACCENT));
        let pressed = ButtonStyle::new(true, true);
        assert_eq!(pressed.fill, ACCENT_PRESSED);
        assert_eq!(pressed.text, primary.text, "label stays readable");
        let secondary = ButtonStyle::new(false, false);
        assert_eq!((secondary.fill, secondary.outline), (FIELD, BORDER));
        assert_eq!(ButtonStyle::new(false, true).fill, FIELD_PRESSED);
    }
}
