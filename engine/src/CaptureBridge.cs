using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

static class CaptureSettings
{
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaiks-Aufnahme");
    public static string FileName { get { return Environment.GetEnvironmentVariable("MOSAIK_CAPTURE_TEST_CONFIG") ?? Path.Combine(Home, "settings.ini"); } }
    static string Read(string key, string fallback)
    {
        var value = new StringBuilder(2048);
        GetPrivateProfileString("capture", key, fallback, value, value.Capacity, FileName);
        return value.ToString();
    }
    static int Number(string key, int fallback)
    {
        int value;
        return int.TryParse(Read(key, fallback.ToString()), out value) ? value : fallback;
    }
    public static string ScreenshotFolder { get { return Read("screenshot_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mosaiks-Screenshots")); } }
    public static string RecordingFolder { get { return Read("recording_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mosaiks-Aufnahmen")); } }
    public static bool Microphone { get { return Number("microphone", 0) != 0; } }
    public static bool SystemAudio { get { return Number("system_audio", 1) != 0; } }
    public static bool Clicks { get { return Number("show_clicks", 1) != 0; } }
    public static int Fps { get { return Number("fps", 60) == 30 ? 30 : 60; } }
    public static int Countdown { get { int n = Number("countdown", 3); return n == 0 || n == 5 ? n : 3; } }
    public static void SaveAudio(bool mic, bool sound, bool clicks)
    {
        IntPtr host = FindWindow("MosaiksAufnahmeRust", null);
        if (host != IntPtr.Zero) PostMessage(host, 0x8014, new IntPtr((mic ? 1 : 0) | (sound ? 2 : 0) | (clicks ? 4 : 0)), IntPtr.Zero);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, int size, string path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
}

static class CaptureBridge
{
    const string Title = "Mosaiks Capture Engine";
    static HwndSource source;
    static Mutex single;

    public static void Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") { CaptureTests.Run(args.Length > 1 ? args[1] : "capture-test.json"); return; }
        if (args.Length > 1 && args[0] == "--live-test") { CaptureLiveTests.Run(args[1]); return; }
        int command = args.Length > 0 && args[0] == "--recording" ? 2 : args.Length > 0 && args[0] == "--idle" ? 0 : 1;
        bool created;
        single = new Mutex(true, "Local\\MosaiksCaptureEngine", out created);
        if (!created)
        {
            for (int i = 0; i < 100; i++)
            {
                IntPtr window = FindWindow(null, Title);
                if (window != IntPtr.Zero) { PostMessage(window, 0x8000 + command, IntPtr.Zero, IntPtr.Zero); return; }
                Thread.Sleep(50);
            }
            return;
        }
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Program.Protect(app);
        source = new HwndSource(new HwndSourceParameters(Title) { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        source.AddHook(Handle);
        app.Dispatcher.BeginInvoke(new Action(delegate { Execute(command); }), DispatcherPriority.ApplicationIdle);
        app.Run();
        source.Dispose();
        single.ReleaseMutex();
        single.Dispose();
    }

    static void Execute(int command)
    {
        if (command == 1) Shot.StartSoon();
        else if (command == 2) Rec.ToggleSoon();
    }

    static IntPtr Handle(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x8001 || message == 0x8002) { handled = true; Execute(message - 0x8000); }
        else if (message == 0x8003 && !Rec.Busy) { handled = true; Application.Current.Shutdown(); }
        else if (message == 0x800A) { handled = true; return new IntPtr((Rec.Busy ? 1 : 0) | (Rec.Live ? 2 : 0)); }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
