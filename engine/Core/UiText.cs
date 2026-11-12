namespace MosaikCapture.Engine.Core;

/// <summary>
/// Every text the user can see. The app's UI language is German; logic files reference these
/// members instead of literals so the texts are in one place (and could be localized later).
/// </summary>
static class UiText
{
    /// <summary>
    /// Window titles. They are part of the German UI and its brand ("Mosaiks-Screenshot", "Mosaiks-Aufnahme"):
    /// Alt+Tab, the taskbar and screen readers show them. The invisible helper windows of a recording
    /// (frame, countdown, toolbar, click ring) are never listed there, but screen readers and UI automation
    /// still read their titles, and tests and QA scripts find all windows by them, so they follow the same scheme.
    /// </summary>
    public static class Titles
    {
        public const string Overlay = "Mosaiks-Screenshot";
        public const string ScreenshotCard = "Mosaiks-Screenshot Karte";
        public const string Editor = "Mosaiks-Screenshot bearbeiten";
        public const string Pin = "Mosaiks-Screenshot angepinnt";
        public const string RecordingFrame = "Mosaiks-Aufnahme Rahmen";
        public const string Countdown = "Mosaiks-Aufnahme Countdown";
        public const string Toolbar = "Mosaiks-Aufnahme Leiste";
        public const string RecordingCard = "Mosaiks-Aufnahme Karte";
        public const string Click = "Mosaiks-Aufnahme Klick";
        public const string Trim = "Aufnahme kürzen";
    }

    /// <summary>Names of the files the user gets.</summary>
    public static class Files
    {
        /// <summary>Default folder below Pictures (user-visible, so German).</summary>
        public const string ScreenshotFolder = "Mosaiks-Screenshots";
        /// <summary>Default folder below Videos.</summary>
        public const string RecordingFolder = "Mosaiks-Aufnahmen";
        public const string ScreenshotPrefix = "Mosaiks-Screenshot ";
        public const string RecordingPrefix = "Mosaiks-Aufnahme ";
        public const string TrimmedSuffix = " gekürzt";
        public const string SaveAsTitle = "Mosaiks-Screenshot speichern unter";
        public const string SaveAsFilter = "PNG-Bild (*.png)|*.png|JPEG-Bild (*.jpg)|*.jpg";
    }

    public static class Common
    {
        public const string Folder = "Ordner";
        public const string Delete = "Löschen";
        public const string Deleted = "Gelöscht";
        public const string InRecycleBin = "Liegt im Papierkorb";
        public const string CopiedToClipboard = "In Zwischenablage kopiert";
        public const string CouldNotSave = "Konnte nicht speichern";
        public const string ReadingText = "Text wird gelesen …";
        public const string NoTextFound = "Kein Text gefunden";
        public const string TextRecognitionUnavailable = "Texterkennung nicht möglich";
        public const string TextCopied = "Text kopiert";
        public const string UnknownError = "Unbekannter Fehler";
        public const string CopyFailed = "Kopieren nicht möglich";
        public const string FfmpegTimedOut = "ffmpeg hat nicht rechtzeitig geantwortet";
        public const string Saving = "Wird gespeichert …";

        public static string Size(int width, int height) => width + " × " + height;

        /// <summary>"3 Zeilen · 42 Zeichen"</summary>
        public static string TextSummary(int lines, int characters) => lines + (lines == 1 ? " Zeile · " : " Zeilen · ") + characters + " Zeichen";
    }

    public static class Overlay
    {
        public const string ScreenshotHint = "Ziehen = Bereich   ·   Klick = Fenster   ·   Leertaste = ganzer Bildschirm   ·   C = Farbe kopieren   ·   Esc = abbrechen";
        public const string RecordingHint = "Aufnahme:   Ziehen = Bereich   ·   Klick = Fenster   ·   Leertaste = ganzer Bildschirm   ·   Esc = abbrechen";
        public const string ColorCopiedSuffix = "  kopiert";
    }

    public static class ScreenshotCard
    {
        public const string Edit = "Bearbeiten";
        public const string Pin = "Anpinnen";
        public const string Text = "Text";
        public const string EditedAndCopied = "Bearbeitet und kopiert";
        public const string NotSaved = "Nicht gespeichert";
        public const string SavedNotCopied = "Gespeichert, nicht kopiert";
        public const string Problem = "Screenshot fehlgeschlagen";
        public const string ScreenUnavailable = "Der Bildschirm lässt sich gerade nicht lesen (z. B. gesperrt)";
        public static string FolderNotWritable(string folder) => "Ordner nicht beschreibbar: " + folder;
    }

    public static class Editor
    {
        /// <summary>Tool names in the order of <c>ScreenshotEditor.Tool</c>.</summary>
        public static readonly string[] ToolNames =
        {
            "Verschieben", "Pfeil", "Rechteck", "Kreis", "Linie", "Stift", "Textmarker", "Scheinwerfer",
            "Text", "Text mit Hintergrund", "Nummer", "Verpixeln", "Weichzeichnen"
        };
        public static readonly string[] WidthNames = { "Sehr fein", "Fein", "Mittel", "Dick", "Sehr dick" };
        public const string MoreHint = "   ·   Klick: mehr";
        public const string Undo = "Rückgängig   Strg+Z";
        public const string Redo = "Wiederholen   Strg+Y";
        public const string SaveAs = "Speichern unter   Strg+S";
        public const string Copy = "Kopieren   Strg+C";
        public const string CopyText = "Text kopieren   Strg+Umschalt+C";
        public const string Cancel = "Abbrechen   Esc";
        public const string Done = "Fertig   Enter";
        public const string Pin = "Anpinnen   F3";
        public const string WidthHint = "   + / −";
        public const string ConfirmDiscard = "Nochmal Esc = Änderungen verwerfen";
        public const string CommandFailed = "Das hat nicht geklappt";

        public static string ColorTip(int number) => "Farbe   " + number;
        public static string SizeNote(string widthName) => "Größe: " + widthName;
        public static string Saved(string fileName) => "Gespeichert: " + fileName;
        public static string TextCopied(string summary) => Common.TextCopied + " · " + summary;
    }

    public static class Pin
    {
        public const string Copy = "Kopieren";
        public const string SaveAs = "Speichern unter …";
        public const string OriginalSize = "Originalgröße";
        public const string Close = "Schließen";
        public const string CloseTip = "Schließen   Esc";
        public const string CloseAll = "Alle Pins schließen";
        public const string CopyShortcut = "Strg+C";
        public const string SaveAsShortcut = "Strg+S";
        public const string Copied = "Kopiert";
        public const string Saved = "Gespeichert";
        public static string Opacity(double percent) => "Deckkraft " + percent + " %";
        public static string Zoom(double percent) => percent + " %";
    }

    public static class Recording
    {
        public const string FfmpegMissing = "ffmpeg fehlt";
        public const string FfmpegInstallHint = "Für Aufnahmen: winget install ffmpeg";
        public const string NotPossible = "Aufnahme nicht möglich";
        public const string MonitorUnreachable = "Dieser Monitor ist für die Aufnahme nicht erreichbar";
        public const string MonitorOnOtherGpu = "Dieser Monitor hängt an einer anderen Grafikkarte; aufnehmen lässt sich nur der Hauptgrafikkarten-Monitor";
        public const string Failed = "Aufnahme fehlgeschlagen";
        public const string Saved = "Aufnahme gespeichert";
        public const string SavedWithoutAudio = "Aufnahme ohne Ton gespeichert";
        public const string NoFrames = "Es wurde kein Bild aufgenommen.";
        public const string StoppedUnexpectedly = "Die Aufnahme wurde unerwartet beendet.";
        public const string AudioLost = "Der Ton konnte nicht aufgenommen werden.";
        public const string CreatingGif = "GIF wird erstellt …";
        public const string GifFailed = "GIF fehlgeschlagen";
        public const string GifSaved = "GIF gespeichert und kopiert";
        public const string GifSavedNotCopied = "GIF gespeichert, nicht kopiert";
        public const string TrimmedNotCopied = "Gekürzt gespeichert, nicht kopiert";
        public const string SavedNotCopied = "Aufnahme gespeichert, nicht kopiert";
        public const string Aborted = "Aufnahme abgebrochen";
        public const string TrimmedAndCopied = "Gekürzt und kopiert";
        public const string Trim = "Kürzen";
        public const string Gif = "GIF";
        public const string CountdownHint = "Esc = abbrechen";

        public static string Recorded(string duration) => duration + " aufgenommen";
        public static string FileInfo(string fileName, string size) => fileName + " · " + size;
        public static string Length(bool gif, string duration) => (gif ? "GIF · " : "") + duration;
    }

    public static class Toolbar
    {
        public const string Move = "Verschieben";
        public const string MicrophoneOn = "Mikro wird aufgenommen";
        public const string MicrophoneOff = "Mikro aus";
        public const string SoundOn = "Systemton wird aufgenommen";
        public const string SoundOff = "Systemton aus";
        public const string ClicksOn = "Klicks werden gezeigt";
        public const string ClicksOff = "Klicks nicht zeigen";
        public const string Resume = "Fortsetzen";
        public const string Pause = "Pause";
        public const string Stop = "Beenden und speichern";
        public const string Discard = "Verwerfen";
        public const string ConfirmDiscard = "Nochmal klicken = verwerfen";
    }

    public static class Trim
    {
        public const string KeyHint = "Leertaste abspielen  ·  I / O Anfang / Ende  ·  ← → Schritt  ·  Enter speichern";
        public const string Sound = "Ton";
        public const string AsGif = "Als GIF";
        public const string Save = "Speichern";

        public static string Selection(string length) => "Auswahl " + length;
        public static string NotSaved(string reason) => "Nicht gespeichert: " + reason;
        public static string Progress(string label, double fraction) => label + "  " + (int)Math.Round(fraction * 100) + " %";
        public static string VideoInfo(string fileName, string duration, int width, int height) => fileName + " · " + duration + " · " + Common.Size(width, height);
    }
}
