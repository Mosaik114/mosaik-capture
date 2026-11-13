//! Layout of the settings window: control ids, positions (in 96-DPI units) and
//! DPI scaling.
//!
//! Controls are created in table order, which is also the keyboard tab order. Labels
//! are German UI texts; doc comments give their English meaning.

/// Client-area width in 96-DPI units.
pub const CLIENT_WIDTH: i32 = 730;
pub const CLIENT_HEIGHT: i32 = 688;
/// Corner radius of owner-drawn buttons in 96-DPI units.
pub const BUTTON_RADIUS: i32 = 10;
/// Distance of the focus rectangle from a button's edge in 96-DPI units.
pub const FOCUS_INSET: i32 = 4;
/// Maximum length of text in edit fields (paths and hotkeys).
pub const EDIT_LIMIT: usize = 511;

/// Control identifiers (also used as tray-menu command ids where they overlap).
pub mod id {
    /// Static labels do not need an id.
    pub const NONE: i32 = 0;
    pub const SCREENSHOT_HOTKEY: i32 = 10;
    pub const SCREENSHOT_FOLDER: i32 = 11;
    /// "Auswählen" ("Choose…") for the screenshot folder.
    pub const BROWSE_SCREENSHOT_FOLDER: i32 = 12;
    pub const OPEN_SCREENSHOT_FOLDER: i32 = 13;
    pub const RECORDING_HOTKEY: i32 = 20;
    pub const RECORDING_FOLDER: i32 = 21;
    /// "Auswählen" ("Choose…") for the recording folder.
    pub const BROWSE_RECORDING_FOLDER: i32 = 22;
    pub const OPEN_RECORDING_FOLDER: i32 = 23;
    pub const FRAME_RATE: i32 = 24;
    pub const MICROPHONE: i32 = 25;
    pub const SYSTEM_AUDIO: i32 = 26;
    pub const SHOW_CLICKS: i32 = 27;
    pub const COUNTDOWN: i32 = 29;
    pub const AUTOSTART: i32 = 30;
    /// "Speichern" ("Save").
    pub const SAVE: i32 = 90;
    pub const STATUS: i32 = 92;
    /// "Screenshot erstellen" ("Take screenshot"; button and tray menu).
    pub const TAKE_SCREENSHOT: i32 = 100;
    /// "Aufnahme starten / beenden" ("Start / stop recording"; button and tray menu).
    pub const TOGGLE_RECORDING: i32 = 101;
    pub const MENU_SETTINGS: i32 = 200;
    pub const MENU_QUIT: i32 = 201;
}

/// Which font a label uses.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontRole {
    Body,
    /// The large tagline at the top.
    Title,
    Heading,
}

/// The kind of a control, with kind-specific data.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ControlKind {
    Label(FontRole),
    Edit,
    /// Owner-drawn push button.
    Button,
    Checkbox,
    /// Drop-down list with the given entries.
    Combo(&'static [&'static str]),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ControlSpec {
    pub kind: ControlKind,
    pub id: i32,
    pub text: &'static str,
    /// Bounds in 96-DPI units.
    pub rect: [i32; 4],
}

impl ControlSpec {
    /// The font of the control: labels choose theirs, everything else uses body text.
    pub const fn font_role(&self) -> FontRole {
        match self.kind {
            ControlKind::Label(role) => role,
            _ => FontRole::Body,
        }
    }
}

const fn label(text: &'static str, role: FontRole, rect: [i32; 4]) -> ControlSpec {
    ControlSpec { kind: ControlKind::Label(role), id: id::NONE, text, rect }
}

const fn edit(id: i32, x: i32, y: i32, width: i32) -> ControlSpec {
    ControlSpec { kind: ControlKind::Edit, id, text: "", rect: [x, y, width, 30] }
}

const fn button(text: &'static str, id: i32, rect: [i32; 4]) -> ControlSpec {
    ControlSpec { kind: ControlKind::Button, id, text, rect }
}

const fn checkbox(text: &'static str, id: i32, x: i32, y: i32, width: i32) -> ControlSpec {
    ControlSpec { kind: ControlKind::Checkbox, id, text, rect: [x, y, width, 28] }
}

const fn combo(
    entries: &'static [&'static str],
    id: i32,
    x: i32,
    y: i32,
    width: i32,
) -> ControlSpec {
    // The height includes the drop-down list.
    ControlSpec { kind: ControlKind::Combo(entries), id, text: "", rect: [x, y, width, 140] }
}

/// Entries of the frame-rate combo box ("30/60 frames per second"), in
/// [`crate::config::FrameRate::index`] order.
const FRAME_RATES: &[&str] = &["30 Bilder/s", "60 Bilder/s"];
/// Entries of the countdown combo box ("No countdown", "3 seconds", "5 seconds"), in
/// [`crate::config::Countdown::index`] order.
const COUNTDOWNS: &[&str] = &["Ohne Vorlauf", "3 Sekunden", "5 Sekunden"];

/// All controls of the settings window, in creation (= tab) order.
pub const CONTROLS: &[ControlSpec] = &[
    label("Festhalten, was zählt.", FontRole::Title, [28, 22, 670, 36]),
    label("Screenshots und Bildschirmaufnahmen", FontRole::Body, [29, 63, 650, 25]),
    // Screenshot section
    label("Screenshot", FontRole::Heading, [28, 112, 650, 28]),
    label("Speicherort", FontRole::Body, [28, 151, 600, 22]),
    edit(id::SCREENSHOT_FOLDER, 28, 176, 532),
    button("Auswählen", id::BROWSE_SCREENSHOT_FOLDER, [572, 176, 130, 30]),
    label("Tastenkürzel", FontRole::Body, [28, 225, 140, 24]),
    edit(id::SCREENSHOT_HOTKEY, 170, 220, 200),
    button("Screenshot erstellen", id::TAKE_SCREENSHOT, [404, 220, 298, 34]),
    // Recording section
    label("Bildschirmaufnahme", FontRole::Heading, [28, 285, 650, 28]),
    label("Speicherort", FontRole::Body, [28, 324, 600, 22]),
    edit(id::RECORDING_FOLDER, 28, 349, 532),
    button("Auswählen", id::BROWSE_RECORDING_FOLDER, [572, 349, 130, 30]),
    label("Tastenkürzel", FontRole::Body, [28, 398, 140, 24]),
    edit(id::RECORDING_HOTKEY, 170, 393, 200),
    button("Aufnahme starten / beenden", id::TOGGLE_RECORDING, [404, 393, 298, 34]),
    checkbox("Mikrofon", id::MICROPHONE, 28, 445, 170),
    checkbox("Systemton", id::SYSTEM_AUDIO, 220, 445, 170),
    checkbox("Mausklicks anzeigen", id::SHOW_CLICKS, 412, 445, 290),
    label("Bildrate", FontRole::Body, [28, 490, 85, 25]),
    combo(FRAME_RATES, id::FRAME_RATE, 115, 485, 135),
    label("Vorlauf", FontRole::Body, [285, 490, 90, 25]),
    combo(COUNTDOWNS, id::COUNTDOWN, 375, 485, 150),
    // General
    checkbox("Mit Windows starten", id::AUTOSTART, 28, 534, 380),
    button("Speichern", id::SAVE, [28, 583, 190, 38]),
    button("Screenshot-Ordner", id::OPEN_SCREENSHOT_FOLDER, [232, 583, 220, 38]),
    button("Aufnahme-Ordner", id::OPEN_RECORDING_FOLDER, [466, 583, 236, 38]),
    ControlSpec {
        kind: ControlKind::Label(FontRole::Body),
        id: id::STATUS,
        text: crate::ui::strings::status::READY,
        rect: [28, 636, 675, 36],
    },
];

/// Whether an owner-drawn button uses the accent colour (primary actions).
pub const fn is_accent_button(control: i32) -> bool {
    matches!(control, id::SAVE | id::TAKE_SCREENSHOT | id::TOGGLE_RECORDING)
}

/// Top-left position that centres a `width` × `height` window in the work area given
/// as `[left, top, right, bottom]`.
pub const fn centered(area: [i32; 4], width: i32, height: i32) -> (i32, i32) {
    let [left, top, right, bottom] = area;
    (left + (right - left - width) / 2, top + (bottom - top - height) / 2)
}

/// Scales 96-DPI layout units to physical pixels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Dpi(i32);

impl Dpi {
    /// The reference DPI of all layout values.
    pub const BASE: i32 = 96;

    /// Wraps a DPI value as reported by `GetDpiForWindow`; 0 or out-of-range values
    /// fall back to 96.
    pub fn new(dpi: u32) -> Self {
        match i32::try_from(dpi) {
            Ok(dpi) if dpi > 0 => Self(dpi),
            _ => Self(Self::BASE),
        }
    }

    /// The DPI value, for APIs such as `AdjustWindowRectExForDpi`.
    pub const fn value(self) -> u32 {
        self.0.cast_unsigned()
    }

    /// Scales `value`, rounding half up.
    pub const fn scale(self, value: i32) -> i32 {
        (value * self.0 + Self::BASE / 2) / Self::BASE
    }

    pub const fn scale_rect(self, [x, y, width, height]: [i32; 4]) -> [i32; 4] {
        [self.scale(x), self.scale(y), self.scale(width), self.scale(height)]
    }

    /// The client-area size of the settings window at this DPI.
    pub const fn client_size(self) -> (i32, i32) {
        (self.scale(CLIENT_WIDTH), self.scale(CLIENT_HEIGHT))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Countdown, FrameRate};

    #[test]
    fn rects_and_client_area_scale_per_monitor() {
        let status = CONTROLS.iter().find(|c| c.id == id::STATUS).unwrap();
        assert_eq!(Dpi::new(96).scale_rect(status.rect), status.rect);
        assert_eq!(Dpi::new(144).scale_rect(status.rect), [42, 954, 1013, 54]);
        assert_eq!(Dpi::new(192).scale_rect(status.rect), [56, 1272, 1350, 72]);
        assert_eq!(Dpi::new(96).client_size(), (730, 688));
        assert_eq!(Dpi::new(120).client_size(), (913, 860));
        assert_eq!(Dpi::new(192).client_size(), (1460, 1376));
        assert_eq!(Dpi::new(144).value(), 144);
        assert_eq!(Dpi::new(0), Dpi::new(96));
    }

    #[test]
    fn scaling_rounds_like_before() {
        assert_eq!(Dpi::new(96).scale(15), 15);
        assert_eq!(Dpi::new(120).scale(10), 13);
        assert_eq!(Dpi::new(144).scale(15), 23);
        assert_eq!(Dpi::new(192).scale(730), 1460);
        assert_eq!(Dpi::new(u32::MAX).scale(10), 10, "nonsense DPI falls back to 96");
    }

    #[test]
    fn centering_in_work_area() {
        assert_eq!(centered([0, 0, 1920, 1040], 760, 720), (580, 160));
        // Work area offset by a taskbar on the left/top.
        assert_eq!(centered([60, 40, 1980, 1080], 760, 720), (640, 200));
        // Larger than the work area: overhang is split evenly.
        assert_eq!(centered([0, 0, 800, 600], 1000, 700), (-100, -50));
    }

    #[test]
    fn ids_are_unique_except_labels() {
        let mut ids: Vec<i32> = CONTROLS.iter().map(|c| c.id).filter(|&i| i != id::NONE).collect();
        let count = ids.len();
        ids.sort_unstable();
        ids.dedup();
        assert_eq!(ids.len(), count);
    }

    #[test]
    fn controls_fit_in_client_area() {
        for control in CONTROLS {
            let [x, y, width, _] = control.rect;
            assert!(x >= 0 && y >= 0, "{control:?}");
            assert!(x + width <= CLIENT_WIDTH, "{control:?}");
            if !matches!(control.kind, ControlKind::Combo(_)) {
                assert!(y + control.rect[3] <= CLIENT_HEIGHT, "{control:?}");
            }
        }
    }

    #[test]
    fn combo_entries_match_config_indices() {
        assert_eq!(FRAME_RATES[FrameRate::Fps30.index()], "30 Bilder/s");
        assert_eq!(FRAME_RATES[FrameRate::Fps60.index()], "60 Bilder/s");
        assert_eq!(COUNTDOWNS[Countdown::Off.index()], "Ohne Vorlauf");
        assert_eq!(COUNTDOWNS[Countdown::Seconds3.index()], "3 Sekunden");
        assert_eq!(COUNTDOWNS[Countdown::Seconds5.index()], "5 Sekunden");
    }

    #[test]
    fn accent_buttons() {
        assert!(is_accent_button(id::SAVE));
        assert!(is_accent_button(id::TAKE_SCREENSHOT));
        assert!(is_accent_button(id::TOGGLE_RECORDING));
        assert!(!is_accent_button(id::BROWSE_SCREENSHOT_FOLDER));
        assert!(!is_accent_button(id::OPEN_RECORDING_FOLDER));
    }
}
