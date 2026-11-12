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

// ffmpeg finden, starten (versteckt, stirbt mit uns) und den Encoder waehlen
static class FF
{
    public static readonly string Exe = Find("ffmpeg.exe"), Probe = Find("ffprobe.exe");
    static string encoder = "";
    static Task detect;
    static IntPtr job;

    public static bool Nvenc { get { return encoder == "nvenc"; } }

    // Waehrend der Aufnahme: schnell und in guter Qualitaet
    public static string LiveVideo
    {
        get { return Nvenc ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 22 -b:v 0 -g 120" : "-c:v libx264 -preset veryfast -crf 20 -g 120"; }
    }

    // Beim Kuerzen: etwas gruendlicher
    public static string FileVideo
    {
        get { return (Nvenc ? "-c:v h264_nvenc -preset p5 -rc vbr -cq 21 -b:v 0" : "-c:v libx264 -preset medium -crf 20") + " -pix_fmt yuv420p"; }
    }

    // ffmpeg bekommt jede Aufnahme. Darum zuerst die festen Installationsorte (winget, Programme,
    // Chocolatey, Scoop) und aus PATH nur vollstaendige lokale Pfade: Ein relativer Eintrag wie "."
    // oder eine Netzwerkfreigabe koennte sonst eine fremde ffmpeg.exe unterschieben.
    static string Find(string name)
    {
        return Find(name, Orte());
    }

    internal static IEnumerable<string> Orte()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) yield return d;
    }

    internal static string Find(string name, IEnumerable<string> dirs)
    {
        foreach (string roh in dirs)
        {
            string d = (roh ?? "").Trim().Trim('"');
            if (!Lokal(d)) continue;
            try
            {
                string p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }   // ungueltiger Eintrag in PATH: ueberspringen
        }
        return null;
    }

    // Nur "C:\..." zaehlt: kein ".\", kein "ordner", kein "C:ordner" (relativ zum Laufwerk), kein "\\server\..."
    internal static bool Lokal(string d)
    {
        return d.Length >= 3 && char.IsLetter(d[0]) && d[1] == ':' && (d[2] == '\\' || d[2] == '/');
    }

    // Grafikkarten-Encoder vorhanden? Einmal pruefen, dann merken
    public static Task Detect()
    {
        if (detect == null)
            detect = Task.Factory.StartNew(delegate
            {
                string o, e;
                encoder = Run(Exe, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.2 -frames:v 2 -c:v h264_nvenc -f null -",
                    null, out o, out e) == 0 ? "nvenc" : "x264";
            });
        return detect;
    }

    public static Process Start(string exe, string args, Action<string> line, Action<string> err)
    {
        var p = new Process
        {
            EnableRaisingEvents = true,
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        p.OutputDataReceived += (o, e) => { if (e.Data != null && line != null) line(e.Data); };
        p.ErrorDataReceived += (o, e) => { if (e.Data != null && err != null) err(e.Data); };
        p.Start();
        Adopt(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    // Laeuft bis zum Ende (im Hintergrund aufrufen); progress bekommt die Sekunden aus -progress
    public static int Run(string exe, string args, Action<double> progress, out string output, out string error)
    {
        var o = new StringBuilder();
        var e = new StringBuilder();
        var p = Start(exe, args, l =>
        {
            lock (o) o.AppendLine(l);
            long us;
            if (progress != null && l.StartsWith("out_time_us=") && long.TryParse(l.Substring(12), out us) && us >= 0) progress(us / 1e6);
        }, l => { lock (e) if (e.Length < 8000) e.AppendLine(l); });
        p.WaitForExit();
        p.WaitForExit();   // ohne Zeitangabe wartet es auch, bis die Ausgabe ganz gelesen ist
        int code = p.ExitCode;
        p.Dispose();
        lock (o) output = o.ToString();
        lock (e) error = e.ToString().Trim();
        return code;
    }

    // In ein Job-Objekt: endet Mosaiks-Suche, endet auch ffmpeg (keine verwaisten Aufnahmen)
    static void Adopt(Process p)
    {
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                int size = IntPtr.Size == 8 ? 144 : 112;   // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                IntPtr info = Marshal.AllocHGlobal(size);
                for (int i = 0; i < size; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteInt32(info, 16, 0x2000);      // LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(job, 9, info, (uint)size);
                Marshal.FreeHGlobal(info);
            }
            AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception ex) { Program.Log("ffmpeg an die Suche binden", ex); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr sa, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

// Die Desktop-Duplizierung liefert das erste Bild erst, wenn sich auf dem Monitor etwas aendert; bis dahin
// wartet ffmpeg (und reagiert nicht einmal auf "q"). Steht der Bildschirm still, stoesst ein winziges, fast
// durchsichtiges Fenster (2 x 2 Pixel, Deckkraft 1/255) die erste Aufnahme an. Mit eigenem Thread und eigener
// Nachrichtenschleife, damit es unabhaengig vom Rest laeuft.
static class RecNudge
{
    public static void Poke(int x, int y)
    {
        Program.Background("Aufnahme anstossen", () => Run(x, y));
    }

    static void Run(int x, int y)
    {
        // WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | TOPMOST | NOACTIVATE; WS_POPUP
        IntPtr h = CreateWindowEx(0x00080000 | 0x20 | 0x80 | 0x8 | 0x08000000, "STATIC", "", 0x80000000u, x, y, 2, 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (h == IntPtr.Zero) return;
        SetLayeredWindowAttributes(h, 0, 1, 2);   // LWA_ALPHA
        for (int i = 0; i < 12; i++)
        {
            ShowWindow(h, i % 2 == 0 ? 4 : 0);   // SW_SHOWNOACTIVATE / SW_HIDE
            Pump(120);
        }
        DestroyWindow(h);
    }

    static void Pump(int ms)
    {
        var end = Environment.TickCount + ms;
        MSG m;
        while (unchecked(end - Environment.TickCount) > 0)
        {
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
}

// Uhren: Mikrosekunden seit 1970 (fuer ffmpeg) und 100-ns-Schritte des Leistungszaehlers (wie WASAPI)
static class RecClock
{
    public static long EpochUs()
    {
        long ft;
        GetSystemTimePreciseAsFileTime(out ft);
        return (ft - 116444736000000000L) / 10;
    }

    public static long Now100() { return (long)(Stopwatch.GetTimestamp() * (10000000.0 / Stopwatch.Frequency)); }

    [DllImport("kernel32.dll")] static extern void GetSystemTimePreciseAsFileTime(out long ft);
}

// Welcher Ausgang der Grafikkarte zeigt diesen Monitor? (ddagrab zaehlt die Ausgaenge des ersten Adapters)
static class Dxgi
{
    public static bool Find(ShotOverlay.RECT mon, out int index, out ShotOverlay.RECT desk)
    {
        index = -1;
        desk = mon;
        IntPtr f;
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out f) != 0) return false;
        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(f);
        Marshal.Release(f);
        IDXGIAdapter adapter = null;
        try
        {
            if (factory.EnumAdapters(0, out adapter) != 0) return false;
            for (uint i = 0; i < 16; i++)
            {
                IDXGIOutput o;
                if (adapter.EnumOutputs(i, out o) != 0) break;
                DXGI_OUTPUT_DESC d;
                int hr = o.GetDesc(out d);
                Marshal.ReleaseComObject(o);
                if (hr != 0) continue;
                if (d.Desktop.left == mon.left && d.Desktop.top == mon.top && d.Desktop.right == mon.right && d.Desktop.bottom == mon.bottom)
                {
                    index = (int)i;
                    desk = d.Desktop;
                    return true;
                }
            }
            return false;
        }
        catch (Exception) { return false; }
        finally
        {
            if (adapter != null) Marshal.ReleaseComObject(adapter);
            Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ShotOverlay.RECT Desktop;
        public int Attached, Rotation;
        public IntPtr Monitor;
    }

    // Nur die benoetigten Methoden; die Platzhalter halten die Reihenfolge ein
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int EnumAdapters(uint i, out IDXGIAdapter a);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int EnumOutputs(uint i, out IDXGIOutput o);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC d);
    }

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
