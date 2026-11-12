// Mosaiks-Bildschirmaufnahme: Bereich waehlen wie beim Screenshot, 3-2-1, dann nimmt ffmpeg ueber
// die Grafikkarte auf (Desktop Duplication, NVENC). Systemton und Mikro nehmen wir selbst ueber WASAPI
// auf und legen sie beim Speichern zeitgenau unter das Bild.
// Ausloeser: Win+Shift+R (Tastenbelegung.ahk sendet Strg+Alt+Shift+F10) oder das Feld unten im Aktionsring;
// derselbe Weg beendet eine laufende Aufnahme.
// Waehrend der Aufnahme: Rahmen um den Bereich und eine Leiste (Zeit, Mikro, Ton, Klicks, Pause, Stopp,
// Verwerfen). Beides ist im Video unsichtbar; Mausklicks erscheinen dagegen als Kreise im Video.
// Danach: Karte unten rechts (Kuerzen, GIF, Ordner, Loeschen, in andere Programme ziehen) und ein Fenster zum Kuerzen.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

// ---------- Ablauf ----------

static class Rec
{
    enum Phase { Idle, Picking, Counting, Running, Saving }
    static Phase phase;
    static ShotOverlay overlay;
    static RecSession session;
    static RecFrame frame;
    static RecBar bar;
    static RecCountdown countdown;
    static RecClicks clicks;
    static RecCard card;
    static RecEditor editor;
    static ShotOverlay.RECT monitor, work;

    // Einstellungen, bleiben zwischen den Aufnahmen erhalten
    public static bool Mic, Sound = true, Clicks = true;

    public static bool Busy { get { return phase != Phase.Idle; } }

    public static bool Live { get { return phase == Phase.Counting || phase == Phase.Running; } }
    public static bool Paused { get { return session != null && session.Paused; } }
    public static double Elapsed { get { return session != null ? session.Elapsed : 0; } }
    public static float MicLevel { get { return session != null && Mic ? session.Audio.MicLevel : 0; } }

    public static string Folder
    {
        get { return CaptureSettings.RecordingFolder; }
    }

    static string Home
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaiks-Aufnahme"); }
    }

    static RecCard Card { get { if (card == null) card = new RecCard(); return card; } }
    static RecFrame Frame { get { if (frame == null) frame = new RecFrame(); return frame; } }
    static RecBar Bar { get { if (bar == null) bar = new RecBar(); return bar; } }
    static RecCountdown Countdown { get { if (countdown == null) countdown = new RecCountdown(); return countdown; } }
    static RecClicks Clicker { get { if (clicks == null) clicks = new RecClicks(); return clicks; } }
    static RecEditor Editor { get { if (editor == null) editor = new RecEditor(); return editor; } }

    // Hotkey und Ring: starten, waehrend des Zaehlens abbrechen, waehrend der Aufnahme beenden
    public static void Toggle()
    {
        if (phase == Phase.Idle) Pick();
        else if (phase == Phase.Counting) { Countdown.Stop(); Abort(); }
        else if (phase == Phase.Running) Stop();
    }

    // Aus dem Ring: kurz warten, bis der ausgeblendete Ring auch wirklich vom Bildschirm ist
    public static void ToggleSoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += delegate { t.Stop(); Toggle(); };
        t.Start();
    }

    static void Pick()
    {
        if (FF.Exe == null || FF.Probe == null)
        {
            Card.Problem("ffmpeg fehlt", "Für Aufnahmen: winget install ffmpeg", RecUi.WorkAtCursor());
            return;
        }
        Load();
        Tidy();
        FF.Detect();   // Encoder schon pruefen, waehrend der Bereich gewaehlt wird
        if (overlay == null)
        {
            overlay = new ShotOverlay(null);
            overlay.Recorded = Picked;
            overlay.Aborted = delegate { if (phase == Phase.Picking) phase = Phase.Idle; };
        }
        phase = Phase.Picking;
        overlay.OpenForRecording();
    }

    static void Picked(Int32Rect r, ShotOverlay.RECT mon, ShotOverlay.RECT wk)
    {
        monitor = mon;
        work = wk;
        r = Fit(r, mon);
        int idx;
        ShotOverlay.RECT desk;
        if (!Dxgi.Find(mon, out idx, out desk))
        {
            phase = Phase.Idle;
            Card.Problem("Aufnahme nicht möglich", "Dieser Monitor ist für die Aufnahme nicht erreichbar", wk);
            return;
        }
        try
        {
            session = new RecSession(r, idx, desk, Mic, Sound);
            session.Broke += Broke;
            session.Audio.Start();   // Ton laeuft schon waehrend des Zaehlens, damit er beim ersten Bild bereit ist
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            session = null;
            phase = Phase.Idle;
            Card.Problem("Aufnahme nicht möglich", ex.Message, wk);
            return;
        }
        phase = Phase.Counting;
        Frame.Present(r, mon);
        Countdown.Run(r, mon, Launch, Go, Abort);
    }

    // Kurz vor der Null: ffmpeg starten, damit das erste Bild genau zum Ende des Zaehlens kommt
    // (das Zaehlfenster selbst ist im Video unsichtbar)
    static void Launch()
    {
        if (session == null) return;
        FF.Detect().Wait(4000);
        session.Resume();
    }

    static void Go()
    {
        if (phase != Phase.Counting || session == null) return;
        phase = Phase.Running;
        Bar.Present(session.Area, work);
        Clicker.Enabled = Clicks;
        Clicker.Start(session.Area, Bar.Handle);
    }

    // Esc beim Zaehlen
    static void Abort()
    {
        if (phase != Phase.Counting) return;
        phase = Phase.Idle;
        Frame.Hide();
        var s = session;
        session = null;
        if (s != null) Task.Factory.StartNew(s.Discard);
    }

    // ffmpeg ist von selbst beendet (z. B. Grafiktreiber, gesperrter Bildschirm): speichern, was da ist
    static void Broke()
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(delegate
        {
            if (phase == Phase.Counting) { Countdown.Stop(); phase = Phase.Running; }
            Stop();
        }));
    }

    public static void Stop()
    {
        if (phase != Phase.Running) return;
        phase = Phase.Saving;
        HideLive();
        var s = session;
        session = null;
        var wk = work;
        Card.Saving(s.Elapsed, wk);
        string path = NewPath(s.Begun);
        Task.Factory.StartNew(() => s.Finish(path)).ContinueWith(t =>
        {
            phase = Phase.Idle;
            if (t.IsFaulted) Program.Log(t.Exception);
            RecResult res = t.IsFaulted ? null : t.Result;
            if (res == null) { Card.Problem("Aufnahme fehlgeschlagen", s.Reason, wk); return; }
            ToClipboard(res.Path);
            Card.Present(res, "Aufnahme gespeichert", wk);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public static void Discard()
    {
        if (phase != Phase.Running) return;
        phase = Phase.Idle;
        HideLive();
        var s = session;
        session = null;
        Task.Factory.StartNew(s.Discard);
    }

    public static void TogglePause()
    {
        if (phase != Phase.Running) return;
        if (session.Paused) session.Resume(); else session.Pause();
        Frame.SetPaused(session.Paused);
        Bar.Refresh();
    }

    public static void SetMic(bool on)
    {
        Mic = on;
        if (session != null) session.Audio.SetMic(on);
        Save();
    }

    public static void SetSound(bool on)
    {
        Sound = on;
        if (session != null) session.Audio.SetSound(on);
        Save();
    }

    public static void SetClicks(bool on)
    {
        Clicks = on;
        Clicker.Enabled = on;
        Save();
    }

    static void HideLive()
    {
        Clicker.Stop();
        Bar.Hide();
        Frame.Hide();
    }

    // ---------- Nach der Aufnahme ----------

    public static void Trim(RecResult r, ShotOverlay.RECT wk)
    {
        Card.HideNow();
        Editor.Open(r, wk);
    }

    public static void MakeGif(RecResult r, ShotOverlay.RECT wk)
    {
        Card.Busy("GIF wird erstellt …");
        Export(r, 0, r.Duration, false, true, p => Card.Progress(p), (res, err) =>
        {
            if (res == null) Card.Problem("GIF fehlgeschlagen", err, wk);
            else Exported(res, "GIF gespeichert und kopiert", wk);
        });
    }

    public static void Exported(RecResult r, string heading, ShotOverlay.RECT wk)
    {
        ToClipboard(r.Path);
        Card.Present(r, heading, wk);
    }

    public static void EditorClosed(RecResult r, ShotOverlay.RECT wk)
    {
        if (File.Exists(r.Path)) Card.Present(r, "Aufnahme gespeichert", wk);
    }

    // Ausschnitt [from, to] als neues MP4 (neu kodiert, bildgenau) oder als GIF; progress 0..1
    public static void Export(RecResult src, double from, double to, bool sound, bool gif,
        Action<double> progress, Action<RecResult, string> done)
    {
        string dir = Path.GetDirectoryName(src.Path), name = Path.GetFileNameWithoutExtension(src.Path);
        string dst = Unique(Path.Combine(dir, name + (gif ? ".gif" : " gekürzt.mp4")));
        double len = Math.Max(0.1, to - from);
        string cut = "-ss " + RecUi.Num(from) + " -to " + RecUi.Num(to) + " -i \"" + src.Path + "\"";
        var disp = Application.Current.Dispatcher;
        double shown = 0;   // ffmpeg meldet auf langsamen Rechnern manchmal kurz eine fruehere Zeit: Balken nie zurueckspringen lassen
        Action<double> report = p =>
        {
            p = Math.Max(0, Math.Min(1, p));
            if (p < shown) return;
            shown = p;
            disp.BeginInvoke(new Action(delegate { progress(p); }));
        };
        Task.Factory.StartNew(delegate
        {
            string o, e;
            int code;
            if (gif)
            {
                // zwei Durchgaenge: erst eine Farbtafel fuer genau diese Bilder, dann das GIF damit
                string pal = Path.Combine(Path.GetTempPath(), "mosaiks-farben-" + Guid.NewGuid().ToString("N") + ".png");
                const string scale = "fps=15,scale='min(960,iw)':-2:flags=lanczos";
                report(0.03);
                code = FF.Run(FF.Exe, "-hide_banner -loglevel error -y " + cut + " -vf \"" + scale + ",palettegen=stats_mode=diff\" \"" + pal + "\"", null, out o, out e);
                if (code == 0)
                {
                    report(0.15);
                    code = FF.Run(FF.Exe, "-hide_banner -loglevel error -y " + cut + " -i \"" + pal + "\" -lavfi \"" + scale +
                        "[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 -progress pipe:1 -nostats \"" + dst + "\"",
                        t => report(0.15 + 0.85 * t / len), out o, out e);
                }
                try { File.Delete(pal); } catch (IOException) { }   // Farbtafel im Temp-Ordner
            }
            else
            {
                string audio = sound && src.Audio ? "-map 0:a:0 -c:a aac -b:a 192k" : "-an";
                code = FF.Run(FF.Exe, "-hide_banner -loglevel error -y " + cut + " -map 0:v:0 " + audio + " " + FF.FileVideo +
                    " -movflags +faststart -progress pipe:1 -nostats \"" + dst + "\"", t => report(t / len), out o, out e);
            }
            if (code != 0)
            {
                try { File.Delete(dst); } catch (IOException) { }   // halbfertige Datei, noch gesperrt
                throw new Exception(RecUi.LastLine(e));
            }
            return RecResult.Load(dst);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { Program.Log(t.Exception); done(null, t.Exception.InnerException.Message); }
            else done(t.Result, null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------- Hilfen ----------

    // Auf den Monitor begrenzen; NVENC braucht eine Mindestgroesse, und das Videoformat gerade Masse
    internal static Int32Rect Fit(Int32Rect r, ShotOverlay.RECT m)
    {
        const int MinW = 160, MinH = 96;
        int x0 = Math.Max(r.X, m.left), y0 = Math.Max(r.Y, m.top);
        int w = Math.Max(0, Math.Min(r.X + r.Width, m.right) - x0), h = Math.Max(0, Math.Min(r.Y + r.Height, m.bottom) - y0);
        if (w < MinW) { x0 = Math.Max(m.left, Math.Min(m.right - MinW, x0 + w / 2 - MinW / 2)); w = MinW; }
        if (h < MinH) { y0 = Math.Max(m.top, Math.Min(m.bottom - MinH, y0 + h / 2 - MinH / 2)); h = MinH; }
        return new Int32Rect(x0, y0, w - w % 2, h - h % 2);
    }

    static string NewPath(DateTime when)
    {
        Directory.CreateDirectory(Folder);
        return Unique(Path.Combine(Folder, "Mosaiks-Aufnahme " + when.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4"));
    }

    internal static string Unique(string p)
    {
        string dir = Path.GetDirectoryName(p), stem = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, stem + " (" + i + ")" + ext);
        return p;
    }

    // Als Datei in die Zwischenablage: Einfuegen in Chat, Mail oder Explorer haengt das Video an
    static void ToClipboard(string path)
    {
        var files = new System.Collections.Specialized.StringCollection();
        files.Add(path);
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetFileDropList(files); return; }
            catch (ExternalException) { Thread.Sleep(40); }
        }
    }

    static void Load()
    {
        Mic = CaptureSettings.Microphone;
        Sound = CaptureSettings.SystemAudio;
        Clicks = CaptureSettings.Clicks;
    }

    static void Save() { CaptureSettings.SaveAudio(Mic, Sound, Clicks); }

    // Reste abgebrochener Aufnahmen (z. B. nach einem Absturz) wegraeumen
    static void Tidy()
    {
        try
        {
            string root = Path.Combine(Home, "Aufnahme");
            if (!Directory.Exists(root)) return;
            foreach (string d in Directory.GetDirectories(root))
                if (DateTime.Now - Directory.GetCreationTime(d) > TimeSpan.FromHours(1))
                    try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // noch belegt: beim naechsten Mal
        }
        catch (IOException) { }   // Ordner gerade nicht lesbar: beim naechsten Mal
    }
}
