namespace MosaikCapture.Engine.Recording;

// Screen recording: pick an area like for a screenshot, count down 3-2-1, then ffmpeg records via
// the GPU (Desktop Duplication, NVENC). System sound and microphone are captured by us via WASAPI
// and laid under the video with exact timing when saving.
// Triggered by the host (see CaptureBridge); the same trigger stops a running recording.
// While recording: a frame around the area and a toolbar (time, microphone, sound, clicks, pause, stop,
// discard). Both are invisible in the video; mouse clicks, however, show up as circles.
// Afterwards: a card in the bottom right (trim, GIF, folder, delete, drag into other apps) and a trim window.
static class Rec
{
    enum Phase { Idle, Picking, Counting, Running, Saving }
    static Phase phase;
    static ShotOverlay? overlay;
    static RecSession? session;
    static RecFrame? frame;
    static RecBar? bar;
    static RecCountdown? countdown;
    static RecClicks? clicks;
    static RecCard? card;
    static RecEditor? editor;
    static ShotOverlay.RECT work;

    // Settings; kept between recordings.
    public static bool Mic, Sound = true, Clicks = true;

    public static bool Busy => phase != Phase.Idle;

    public static bool Live => phase == Phase.Counting || phase == Phase.Running;
    public static bool Paused => session != null && session.Paused;
    public static double Elapsed => session != null ? session.Elapsed : 0;
    public static float MicLevel => session != null && Mic ? session.Audio.MicLevel : 0;

    public static string Folder => CaptureSettings.RecordingFolder;

    static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaiks-Aufnahme");

    static RecCard Card => card ??= new RecCard();
    static RecFrame Frame => frame ??= new RecFrame();
    static RecBar Bar => bar ??= new RecBar();
    static RecCountdown Countdown => countdown ??= new RecCountdown();
    static RecClicks Clicker => clicks ??= new RecClicks();
    static RecEditor Editor => editor ??= new RecEditor();

    // Host trigger: start, cancel during the countdown, stop while recording.
    public static void Toggle()
    {
        if (phase == Phase.Idle) Pick();
        else if (phase == Phase.Counting) { Countdown.Stop(); Abort(); }
        else if (phase == Phase.Running) Stop();
    }

    // Starts after a short delay so that whatever triggered it has left the screen.
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
        FF.Detect();   // probe the encoder while the area is being picked
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
        work = wk;
        r = Fit(r, mon);
        if (!Dxgi.Find(mon, out int idx, out ShotOverlay.RECT desk))
        {
            phase = Phase.Idle;
            Card.Problem("Aufnahme nicht möglich", "Dieser Monitor ist für die Aufnahme nicht erreichbar", wk);
            return;
        }
        try
        {
            session = new RecSession(r, idx, desk, Mic, Sound);
            session.Broke += Broke;
            session.Audio.Start();   // audio already runs during the countdown so it is ready for the first frame
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            session = null;
            phase = Phase.Idle;
            Card.Problem("Aufnahme nicht möglich", ex.Message, wk);
            return;
        }
        phase = Phase.Counting;
        Frame.Present(r, mon);
        Countdown.Run(r, mon, Launch, Go, Abort);
    }

    // Shortly before zero: start ffmpeg so the first frame arrives exactly when the countdown ends
    // (the countdown window itself is invisible in the video).
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

    // Esc during the countdown.
    static void Abort()
    {
        if (phase != Phase.Counting) return;
        phase = Phase.Idle;
        Frame.Hide();
        var s = session;
        session = null;
        if (s != null) Task.Factory.StartNew(s.Discard);
    }

    // ffmpeg exited on its own (e.g. graphics driver, locked screen): save what we have.
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
        var s = session!;
        session = null;
        var wk = work;
        Card.Saving(s.Elapsed, wk);
        string path = NewPath(s.Begun);
        Task.Factory.StartNew(() => s.Finish(path)).ContinueWith(t =>
        {
            phase = Phase.Idle;
            if (t.IsFaulted) ErrorLog.Write(t.Exception);
            RecResult? res = t.IsFaulted ? null : t.Result;
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
        var s = session!;
        session = null;
        Task.Factory.StartNew(s.Discard);
    }

    public static void TogglePause()
    {
        if (phase != Phase.Running) return;
        if (session!.Paused) session.Resume(); else session.Pause();
        Frame.SetPaused(session.Paused);
        Bar.Refresh();
    }

    public static void SetMic(bool on)
    {
        Mic = on;
        session?.Audio.SetMic(on);
        Save();
    }

    public static void SetSound(bool on)
    {
        Sound = on;
        session?.Audio.SetSound(on);
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

    // ---------- After recording ----------

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
            if (res == null) Card.Problem("GIF fehlgeschlagen", err!, wk);
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

    // Exports [from, to] as a new MP4 (re-encoded, frame accurate) or as a GIF; progress is 0..1.
    public static void Export(RecResult src, double from, double to, bool sound, bool gif,
        Action<double> progress, Action<RecResult?, string?> done)
    {
        string dst = ExportTarget(src.Path, gif);
        double len = Math.Max(0.1, to - from);
        string cut = CutInput(src.Path, from, to);
        var disp = Application.Current.Dispatcher;
        double shown = 0;   // on slow machines ffmpeg sometimes briefly reports an earlier time: never let the bar jump back
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
                // two passes: first a palette for exactly these frames, then the GIF using it
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
                try { File.Delete(pal); } catch (IOException) { }   // palette in the temp folder
            }
            else
            {
                string audio = sound && src.Audio ? "-map 0:a:0 -c:a aac -b:a 192k" : "-an";
                code = FF.Run(FF.Exe, "-hide_banner -loglevel error -y " + cut + " -map 0:v:0 " + audio + " " + FF.FileVideo +
                    " -movflags +faststart -progress pipe:1 -nostats \"" + dst + "\"", t => report(t / len), out o, out e);
            }
            if (code != 0)
            {
                try { File.Delete(dst); } catch (IOException) { }   // partial file, still locked
                throw new InvalidOperationException(RecUi.LastLine(e));
            }
            return RecResult.Load(dst);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { ErrorLog.Write(t.Exception); done(null, t.Exception!.InnerException!.Message); }
            else done(t.Result, null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Target file of an export next to the source: "<name> gekürzt.mp4" or "<name>.gif", never overwriting.
    internal static string ExportTarget(string source, bool gif) =>
        Unique(Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + (gif ? ".gif" : " gekürzt.mp4")));

    // ffmpeg input arguments that seek to [from, to] of the source.
    internal static string CutInput(string source, double from, double to) =>
        "-ss " + RecUi.Num(from) + " -to " + RecUi.Num(to) + " -i \"" + source + "\"";

    // ---------- Helpers ----------

    // Clamps to the monitor; NVENC needs a minimum size and the video format needs even dimensions.
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
        return Unique(Path.Combine(Folder, FileName(when)));
    }

    // "Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4"
    internal static string FileName(DateTime when) => "Mosaiks-Aufnahme " + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.CurrentCulture) + ".mp4";

    internal static string Unique(string p)
    {
        string dir = Path.GetDirectoryName(p)!, stem = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, stem + " (" + i + ")" + ext);
        return p;
    }

    // Puts the file on the clipboard: pasting into chat, mail or Explorer attaches the video.
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

    // Removes leftovers of aborted recordings (e.g. after a crash).
    static void Tidy()
    {
        try
        {
            string root = Path.Combine(Home, "Aufnahme");
            if (!Directory.Exists(root)) return;
            foreach (string d in Directory.GetDirectories(root))
                if (DateTime.Now - Directory.GetCreationTime(d) > TimeSpan.FromHours(1))
                    try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // still in use: next time
        }
        catch (IOException) { }   // folder not readable right now: next time
    }
}
