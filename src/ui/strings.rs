//! User-visible German texts (except control labels, which live in the layout table).
//! Each doc comment says what the text means in English.

/// Title of the settings window ("Mosaiks Aufnahme – Settings").
pub const WINDOW_TITLE: &str = "Mosaiks Aufnahme – Einstellungen";
/// Tooltip of the notification-area icon ("… – screenshot and screen recording").
pub const TRAY_TOOLTIP: &str = "Mosaiks Aufnahme – Screenshot und Bildschirmaufnahme";
/// Title of the folder picker ("Choose a folder").
pub const PICK_FOLDER_TITLE: &str = "Speicherordner auswählen";

/// Tray menu: "Take screenshot".
pub const MENU_SCREENSHOT: &str = "Screenshot erstellen";
/// Tray menu: "Start / stop recording".
pub const MENU_RECORDING: &str = "Aufnahme starten / beenden";
/// Tray menu: "Settings".
pub const MENU_SETTINGS: &str = "Einstellungen";
/// Tray menu: "Quit".
pub const MENU_QUIT: &str = "Beenden";

/// Status messages shown below the buttons.
pub mod status {
    /// Initial status ("Ready.").
    pub const READY: &str = "Bereit.";
    /// Settings saved successfully.
    pub const SAVED: &str = "Gespeichert. Beide Tastenkürzel sind aktiv.";
    /// A hotkey could not be parsed.
    pub const INVALID_HOTKEY: &str = "Bitte ein gültiges Kürzel eingeben, etwa Shift+Alt+4.";
    /// A hotkey uses Shift as its only modifier ("A shortcut needs Ctrl or Alt, for
    /// example Shift+Alt+4.").
    pub const SHIFT_ONLY_HOTKEY: &str = "Ein Kürzel braucht Strg oder Alt, etwa Shift+Alt+4.";
    /// Both actions use the same hotkey.
    pub const DUPLICATE_HOTKEY: &str = "Screenshot und Aufnahme brauchen unterschiedliche Kürzel.";
    /// A folder path is incomplete or invalid.
    pub const INVALID_FOLDER: &str = "Bitte vollständige, gültige Ordnerpfade auswählen.";
    /// Registering a new hotkey failed while saving.
    pub const HOTKEY_TAKEN_KEEPING_OLD: &str =
        "Ein Kürzel ist bereits belegt. Die bisherigen Kürzel bleiben aktiv.";
    /// Registering a hotkey failed at startup.
    pub const HOTKEY_TAKEN: &str = "Ein Tastenkürzel ist bereits belegt. Bitte ein anderes wählen.";
    /// Saving the settings or the autostart entry failed.
    pub const SAVE_FAILED: &str = "Die Einstellungen konnten nicht gespeichert werden.";
    /// Saving at startup failed.
    pub const STARTUP_SAVE_FAILED: &str =
        "Die Einstellungen oder der Autostart konnten nicht gespeichert werden.";
    /// Saving audio settings sent by the engine failed.
    pub const AUDIO_SAVE_FAILED: &str = "Audioeinstellungen konnten nicht gespeichert werden.";
    /// The engine could not be reached or started.
    pub const ENGINE_FAILED: &str = "Das Aufnahmeprogramm konnte nicht gestartet werden.";
    /// "Open folder" was clicked, but the folder has not been created yet.
    pub const FOLDER_MISSING: &str = "Der Ordner existiert noch nicht.";
    /// Quitting is blocked by a running capture.
    pub const BUSY: &str = "Bitte die laufende Aufnahme oder Bereichsauswahl zuerst beenden.";
}
