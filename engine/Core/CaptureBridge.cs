namespace MosaikCapture.Engine.Core;

// Process entry and IPC with the Rust host.
//
// Command line: --screenshot (default), --recording, --idle, --self-test <report>, --live-test <dir>.
// Only one engine runs per session (mutex Local\MosaiksCaptureEngine). A second start forwards its
// command to the running instance and exits. The running instance owns a hidden window titled
// "Mosaiks Capture Engine" that accepts:
//   0x8001  take a screenshot
//   0x8002  start / stop a screen recording
//   0x8003  quit (ignored while a recording is busy)
//   0x800A  query state: bit 0 = busy, bit 1 = live (counting down or recording)
static class CaptureBridge
{
    internal const string Title = "Mosaiks Capture Engine";
    internal const string MutexName = "Local\\MosaiksCaptureEngine";
    internal const int CommandBase = 0x8000;
    internal const int ScreenshotMessage = 0x8001, RecordingMessage = 0x8002, QuitMessage = 0x8003, StateMessage = 0x800A;

    static HwndSource? source;
    static Mutex? single;

    public static void Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") { SelfTest.Run(args.Length > 1 ? args[1] : "capture-test.json"); return; }
        if (args.Length > 1 && args[0] == "--live-test") { LiveTest.Run(args[1]); return; }
        int command = ParseCommand(args);
        single = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            for (int i = 0; i < 100; i++)
            {
                IntPtr window = FindWindow(null, Title);
                if (window != IntPtr.Zero) { PostMessage(window, CommandBase + command, IntPtr.Zero, IntPtr.Zero); return; }
                Thread.Sleep(50);
            }
            return;
        }
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Program.Protect(app);
        source = new HwndSource(new HwndSourceParameters(Title) { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });   // WS_POPUP, never shown
        source.AddHook(Handle);
        app.Dispatcher.BeginInvoke(new Action(delegate { Execute(command); }), DispatcherPriority.ApplicationIdle);
        app.Run();
        source.Dispose();
        single.ReleaseMutex();
        single.Dispose();
    }

    // 0 = idle, 1 = screenshot, 2 = recording; added to CommandBase when forwarded to a running instance.
    internal static int ParseCommand(string[] args) =>
        args.Length > 0 && args[0] == "--recording" ? 2 : args.Length > 0 && args[0] == "--idle" ? 0 : 1;

    // Reply to StateMessage.
    internal static int StateFlags(bool busy, bool live) => (busy ? 1 : 0) | (live ? 2 : 0);

    static void Execute(int command)
    {
        if (command == 1) Shot.StartSoon();
        else if (command == 2) Rec.ToggleSoon();
    }

    static IntPtr Handle(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == ScreenshotMessage || message == RecordingMessage) { handled = true; Execute(message - CommandBase); }
        else if (message == QuitMessage && !Rec.Busy) { handled = true; Application.Current.Shutdown(); }
        else if (message == StateMessage) { handled = true; return new IntPtr(StateFlags(Rec.Busy, Rec.Live)); }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
