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

// ---------- Aufnahme selbst: Bildteile mit ffmpeg, Ton mit WASAPI, am Ende zusammensetzen ----------

class RecSession
{
    // Ein Stueck Video zwischen zwei Pausen. K = Startzeit fuer ffmpeg (Mikrosekunden seit 1970),
    // Q = derselbe Moment auf der Uhr der Tonspuren; S/D = Beginn relativ zu K und Laenge laut ffprobe.
    class Seg
    {
        public string File;
        public long K, Q, T0;
        public Process P;
        public Task Done;
        public double S, D;
        public readonly StringBuilder Err = new StringBuilder();
    }

    public readonly Int32Rect Area;
    public readonly AudioRec Audio;
    public readonly string Dir;
    public readonly DateTime Begun = DateTime.Now;
    public string Reason = "Es wurde kein Bild aufgenommen.";
    public event Action Broke;
    readonly int output, ox, oy;
    readonly List<Seg> segs = new List<Seg>();
    Seg cur;
    long done;   // Laenge der abgeschlossenen Teile (Stopwatch-Takte)

    public RecSession(Int32Rect area, int outputIdx, ShotOverlay.RECT desk, bool mic, bool sound)
    {
        Area = area;
        output = outputIdx;
        ox = area.X - desk.left;
        oy = area.Y - desk.top;
        Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mosaiks-Aufnahme", "Aufnahme", Begun.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(Dir);
        Audio = new AudioRec(Dir, mic, sound);
    }

    public bool Paused { get { return cur == null; } }

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
        if (segs.Count > 0 && segs[segs.Count - 1].Done != null) segs[segs.Count - 1].Done.Wait(3000);
        var s = new Seg { File = Path.Combine(Dir, "teil" + segs.Count + ".mkv") };
        // Zeitstempel = Zeit seit K; ffprobe liefert dann den Beginn des ersten Bildes relativ zu K
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
            Program.Log(ex);
            Reason = ex.Message;
            var b = Broke;
            if (b != null) b();
            return;
        }
        segs.Add(s);
        cur = s;
        RecNudge.Poke(Area.X + Area.Width - 2, Area.Y + Area.Height - 2);
        s.P.Exited += delegate
        {
            if (cur != s) return;   // gewollt beendet
            lock (s.Err) Reason = s.Err.Length > 0 ? RecUi.LastLine(s.Err.ToString()) : "Die Aufnahme wurde unerwartet beendet.";
            var b = Broke;
            if (b != null) b();
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

    // "q" beendet ffmpeg sauber (Datei wird ordentlich abgeschlossen); zur Not hart
    static void Quit(Process p)
    {
        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch (Exception) { }   // ffmpeg schon beendet
        if (!p.WaitForExit(6000)) try { p.Kill(); } catch (Exception) { }   // inzwischen doch beendet
        p.WaitForExit();
    }

    public void Discard()
    {
        Pause();
        foreach (var s in segs) if (s.Done != null) s.Done.Wait(8000);
        Audio.Stop();
        try { Directory.Delete(Dir, true); } catch (Exception) { }   // Arbeitsordner noch belegt: Rec.Tidy raeumt ihn spaeter weg
    }

    // Alles zusammensetzen: Bildteile ohne neu zu kodieren aneinander, darunter der passende Ton
    public RecResult Finish(string path)
    {
        Pause();
        foreach (var s in segs) if (s.Done != null) s.Done.Wait();
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

        string o, e;
        if (FF.Run(FF.Exe, args, null, out o, out e) != 0 || !File.Exists(path))
        {
            Reason = RecUi.LastLine(e);
            try { File.Delete(path); } catch (IOException) { }   // noch gesperrt: bleibt als Rest liegen
            return null;
        }
        try { Directory.Delete(Dir, true); } catch (Exception) { }   // Arbeitsordner noch belegt: Rec.Tidy raeumt ihn spaeter weg
        return RecResult.Load(path);
    }

    // ffmpeg-Befehl zum Zusammensetzen: die Bildteile ohne neu zu kodieren aneinander, darunter aus jeder Tonspur
    // genau die Stuecke, in denen Bild aufgenommen wurde (from/len je Teil in Sekunden auf der Uhr der Tonspur)
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
                f.Append(";");
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
        string o, e;
        if (FF.Run(FF.Probe, "-v error -show_entries format=start_time,duration -of default=nw=1 \"" + s.File + "\"", null, out o, out e) != 0) return false;
        s.S = RecUi.Field(o, "start_time");
        s.D = RecUi.Field(o, "duration");
        return s.D > 0.05;
    }
}

// Ergebnis: fertige Datei mit Eckdaten und Vorschaubild
public class RecResult
{
    public string Path;
    public double Duration;
    public int Width, Height;
    public bool Audio, Gif;
    public long Bytes;
    public BitmapSource Thumb;

    public static RecResult Load(string path)
    {
        var r = new RecResult { Path = path, Gif = System.IO.Path.GetExtension(path).ToLowerInvariant() == ".gif" };
        string o, e;
        FF.Run(FF.Probe, "-v error -show_entries format=duration:stream=codec_type,width,height -of default=nw=1 \"" + path + "\"", null, out o, out e);
        r.Duration = RecUi.Field(o, "duration");
        r.Width = (int)RecUi.Field(o, "width");
        r.Height = (int)RecUi.Field(o, "height");
        r.Audio = o.Contains("codec_type=audio");
        r.Bytes = new FileInfo(path).Length;
        r.Thumb = Frame(path, r.Duration / 2, 960);
        return r;
    }

    // Ein Bild aus dem Video, hoechstens maxW Pixel breit
    public static BitmapSource Frame(string path, double at, int maxW)
    {
        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mosaiks-bild-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            string o, e;
            if (FF.Run(FF.Exe, "-hide_banner -loglevel error -y -ss " + RecUi.Num(Math.Max(0, at)) + " -i \"" + path +
                "\" -frames:v 1 -vf \"scale='min(" + maxW + ",iw)':-2\" \"" + png + "\"", null, out o, out e) != 0 || !File.Exists(png)) return null;
            return RecUi.LoadImage(png);
        }
        finally { try { File.Delete(png); } catch (IOException) { } }   // Zwischenbild im Temp-Ordner
    }

    // Filmstreifen fuer das Kuerzen: n Bilder nebeneinander, je h Pixel hoch (schnell: jedes Bild mit eigenem Sprung)
    public static BitmapSource Strip(string path, double dur, int n, int h)
    {
        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mosaiks-streifen-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var a = new StringBuilder("-hide_banner -loglevel error -y");
            var f = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                a.Append(" -ss " + RecUi.Num(dur * (i + 0.5) / n) + " -i \"" + path + "\"");
                f.Append("[" + i + ":v]scale=-2:" + h + ",setsar=1[v" + i + "];");
            }
            for (int i = 0; i < n; i++) f.Append("[v" + i + "]");
            f.Append(n > 1 ? "hstack=inputs=" + n : "null");
            a.Append(" -filter_complex \"" + f + "\" -frames:v 1 \"" + png + "\"");
            string o, e;
            if (FF.Run(FF.Exe, a.ToString(), null, out o, out e) != 0 || !File.Exists(png)) return null;
            return RecUi.LoadImage(png);
        }
        finally { try { File.Delete(png); } catch (IOException) { } }   // Zwischenbild im Temp-Ordner
    }
}
