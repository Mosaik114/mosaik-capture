namespace MosaikCapture.Engine.Recording;

// The recording itself: video segments via ffmpeg, audio via WASAPI, assembled at the end.
class RecSession
{
    // A piece of video between two pauses. K = start time for ffmpeg (microseconds since 1970),
    // Q = the same moment on the audio clock; S/D = start relative to K and duration according to ffprobe.
    class Seg
    {
        public string File = "";
        public long K, Q, T0;
        public Process P = null!;   // set right after construction
        public Task? Done;
        public double S, D;
        public readonly StringBuilder Err = new StringBuilder();
    }

    public readonly Int32Rect Area;
    public readonly AudioRec Audio;
    public readonly string Dir;
    public readonly DateTime Begun = DateTime.Now;
    public string Reason = "Es wurde kein Bild aufgenommen.";
    public event Action? Broke;
    readonly int output, ox, oy;
    readonly List<Seg> segs = new List<Seg>();
    Seg? cur;
    long done;   // length of the finished segments (Stopwatch ticks)

    public RecSession(Int32Rect area, int outputIdx, ShotOverlay.RECT desk, bool mic, bool sound)
    {
        Area = area;
        output = outputIdx;
        ox = area.X - desk.left;
        oy = area.Y - desk.top;
        Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mosaiks-Aufnahme", "Aufnahme", Begun.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.CurrentCulture));
        Directory.CreateDirectory(Dir);
        Audio = new AudioRec(Dir, mic, sound);
    }

    public bool Paused => cur == null;

    public double Elapsed
    {
        get
        {
            long t = done;
            var c = cur;
            if (c != null) t += Stopwatch.GetTimestamp() - c.T0;
            return t / (double)Stopwatch.Frequency;
        }
    }

    public void Resume()
    {
        if (cur != null) return;
        if (segs.Count > 0 && segs[segs.Count - 1].Done != null) segs[segs.Count - 1].Done!.Wait(3000);
        var s = new Seg { File = Path.Combine(Dir, "teil" + segs.Count + ".mkv") };
        // timestamps = time since K; ffprobe then reports the first frame relative to K
        string grab = "ddagrab=output_idx=" + output + ":framerate=" + CaptureSettings.Fps + ":video_size=" + Area.Width + "x" + Area.Height +
            ":offset_x=" + ox + ":offset_y=" + oy;
        string tags = "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709";
        s.K = RecClock.EpochUs();
        s.Q = RecClock.Now100();
        string vf = FF.Nvenc
            ? grab + "," + tags + ",setpts=PTS+(RTCSTART-" + s.K + ")/(1000000*TB)"
            : grab + ",hwdownload,format=bgra,scale=out_color_matrix=bt601:out_range=tv,format=yuv420p," + tags +
              ",setpts=PTS+(RTCSTART-" + s.K + ")/(1000000*TB)";
        string args = "-hide_banner -loglevel error -y -f lavfi -i \"" + vf + "\" -copyts " + FF.LiveVideo + " -f matroska \"" + s.File + "\"";
        s.T0 = Stopwatch.GetTimestamp();
        try
        {
            s.P = FF.Start(FF.Exe, args, null, l => { lock (s.Err) if (s.Err.Length < 4000) s.Err.AppendLine(l); });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Reason = ex.Message;
            Broke?.Invoke();
            return;
        }
        segs.Add(s);
        cur = s;
        RecNudge.Poke(Area.X + Area.Width - 2, Area.Y + Area.Height - 2);
        s.P.Exited += delegate
        {
            if (cur != s) return;   // stopped on purpose
            lock (s.Err) Reason = s.Err.Length > 0 ? RecUi.LastLine(s.Err.ToString()) : "Die Aufnahme wurde unerwartet beendet.";
            Broke?.Invoke();
        };
    }

    public void Pause()
    {
        var s = cur;
        if (s == null) return;
        cur = null;
        done += Stopwatch.GetTimestamp() - s.T0;
        s.Done = Task.Factory.StartNew(() => Quit(s.P));
    }

    // "q" stops ffmpeg cleanly (the file is finalized properly); kill it if necessary.
    static void Quit(Process p)
    {
        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch (Exception) { }   // ffmpeg already exited
        if (!p.WaitForExit(6000)) try { p.Kill(); } catch (Exception) { }   // exited in the meantime
        p.WaitForExit();
    }

    public void Discard()
    {
        Pause();
        foreach (var s in segs) s.Done?.Wait(8000);
        Audio.Stop();
        try { Directory.Delete(Dir, true); } catch (Exception) { }   // work folder still in use: Rec.Tidy removes it later
    }

    // Assembles everything: video segments concatenated without re-encoding, with the matching audio underneath.
    public RecResult? Finish(string path)
    {
        Pause();
        foreach (var s in segs) s.Done?.Wait();
        Audio.Stop();

        var parts = new List<Seg>();
        foreach (var s in segs)
            if (File.Exists(s.File) && new FileInfo(s.File).Length > 0 && Probe(s)) parts.Add(s);
        if (parts.Count == 0)
        {
            foreach (var s in segs) lock (s.Err) if (s.Err.Length > 0) Reason = RecUi.LastLine(s.Err.ToString());
            return null;
        }

        var tracks = new List<string>();
        if (Audio.SysUsed) tracks.Add(Audio.SysFile);
        if (Audio.MicUsed) tracks.Add(Audio.MicFile);
        string args = Assemble(Dir, parts.Select(p => p.File).ToList(), tracks,
            parts.Select(p => Math.Max(0, Audio.Offset(p.Q) + p.S)).ToList(), parts.Select(p => p.D).ToList(), path);

        if (FF.Run(FF.Exe, args, null, out string o, out string e) != 0 || !File.Exists(path))
        {
            Reason = RecUi.LastLine(e);
            try { File.Delete(path); } catch (IOException) { }   // still locked: left behind
            return null;
        }
        try { Directory.Delete(Dir, true); } catch (Exception) { }   // work folder still in use: Rec.Tidy removes it later
        return RecResult.Load(path);
    }

    // ffmpeg arguments for assembling: video segments concatenated without re-encoding, plus from each audio track
    // exactly the pieces during which video was recorded (from/len per segment, in seconds on the audio clock).
    internal static string Assemble(string dir, IList<string> files, IList<string> tracks, IList<double> from, IList<double> len, string path)
    {
        string list = Path.Combine(dir, "teile.txt");
        File.WriteAllLines(list, files.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'"));

        var a = new StringBuilder("-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\"");
        foreach (string t in tracks) a.Append(" -i \"" + t + "\"");
        if (tracks.Count > 0)
        {
            var f = new StringBuilder();
            int n = files.Count;
            for (int j = 0; j < tracks.Count; j++)
            {
                f.Append("[" + (j + 1) + ":a]asplit=" + n);
                for (int i = 0; i < n; i++) f.Append("[s" + j + "_" + i + "]");
                f.Append(';');
                for (int i = 0; i < n; i++)
                    f.Append("[s" + j + "_" + i + "]atrim=start=" + RecUi.Num(from[i]) + ":duration=" + RecUi.Num(len[i]) +
                        ",asetpts=PTS-STARTPTS,apad=whole_dur=" + RecUi.Num(len[i]) + "[t" + j + "_" + i + "];");
                for (int i = 0; i < n; i++) f.Append("[t" + j + "_" + i + "]");
                f.Append("concat=n=" + n + ":v=0:a=1[c" + j + "];");
            }
            f.Append(tracks.Count == 2 ? "[c0][c1]amix=inputs=2:normalize=0:duration=longest[a]" : "[c0]anull[a]");
            a.Append(" -filter_complex \"" + f + "\" -map 0:v -map \"[a]\" -c:a aac -b:a 192k");
        }
        else a.Append(" -map 0:v");
        a.Append(" -c:v copy -movflags +faststart \"" + path + "\"");
        return a.ToString();
    }

    static bool Probe(Seg s)
    {
        if (FF.Run(FF.Probe, "-v error -show_entries format=start_time,duration -of default=nw=1 \"" + s.File + "\"", null, out string o, out string e) != 0) return false;
        s.S = RecUi.Field(o, "start_time");
        s.D = RecUi.Field(o, "duration");
        return s.D > 0.05;
    }
}
