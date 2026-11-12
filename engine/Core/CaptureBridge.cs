namespace MosaikCapture.Engine.Core;

/// <summary>
/// Process entry and IPC with the host (the Rust tray app).
/// </summary>
/// <remarks>
/// Command line: <c>--screenshot</c> (default), <c>--recording</c>, <c>--idle</c>,
/// <c>--self-test &lt;report&gt;</c>, <c>--live-test &lt;dir&gt;</c>.
/// Only one engine runs per session (mutex <see cref="MutexName"/>). A second start forwards its
/// command to the running instance and exits. The running instance owns a hidden window titled
/// <see cref="Title"/> that accepts:
/// <list type="table">
/// <item><term>0x8001</term><description>take a screenshot</description></item>
/// <item><term>0x8002</term><description>start / stop a screen recording</description></item>
/// <item><term>0x8003</term><description>quit (ignored while a recording is busy)</description></item>
/// <item><term>0x800A</term><description>query state: bit 0 = busy, bit 1 = live (counting down or recording)</description></item>
/// </list>
/// </remarks>
internal static class CaptureBridge
{
    internal const string Title = "MosaikCapture.Engine";
    internal const string MutexName = "Local\\MosaikCapture.Engine";
    internal const int CommandBase = 0x8000;
    internal const int ScreenshotMessage = 0x8001, RecordingMessage = 0x8002, QuitMessage = 0x8003, StateMessage = 0x800A;
    const int ForwardAttempts = 100, ForwardDelayMs = 50;

    enum Command { Idle = 0, Screenshot = 1, Recording = 2 }

    public static void Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") { Diagnostics.SelfTest.Run(args.Length > 1 ? args[1] : "capture-test.json"); return; }
        if (args.Length > 1 && args[0] == "--live-test") { Diagnostics.LiveTest.Run(args[1]); return; }
        int command = ParseCommand(args);
        using var single = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            ForwardToRunningInstance(command);
            return;
        }
        try
        {
            RunMessageLoop(command);
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    // Another engine owns the mutex: hand the command over (its window may still be starting up).
    static void ForwardToRunningInstance(int command)
    {
        for (int attempt = 0; attempt < ForwardAttempts; attempt++)
        {
            IntPtr window = NativeMethods.FindWindow(null, Title);
            if (window != IntPtr.Zero)
            {
                NativeMethods.PostMessage(window, CommandBase + command, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            Thread.Sleep(ForwardDelayMs);
        }
    }

    static void RunMessageLoop(int command)
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Program.Protect(app);
        var parameters = new HwndSourceParameters(Title) { Width = 0, Height = 0, WindowStyle = unchecked((int)NativeMethods.WS_POPUP) };   // never shown
        using var window = new HwndSource(parameters);
        window.AddHook(OnMessage);
        app.Dispatcher.BeginInvoke(new Action(() => Execute(command)), DispatcherPriority.ApplicationIdle);
        app.Run();
    }

    /// <summary>0 = idle, 1 = screenshot, 2 = recording; added to <see cref="CommandBase"/> when forwarded.</summary>
    internal static int ParseCommand(string[] args) =>
        (int)(args.Length > 0 && args[0] == "--recording" ? Command.Recording : args.Length > 0 && args[0] == "--idle" ? Command.Idle : Command.Screenshot);

    /// <summary>Reply to <see cref="StateMessage"/>.</summary>
    internal static int StateFlags(bool busy, bool live) => (busy ? 1 : 0) | (live ? 2 : 0);

    static void Execute(int command)
    {
        if (command == (int)Command.Screenshot) Screenshots.StartSoon();
        else if (command == (int)Command.Recording) ScreenRecorder.ToggleSoon();
    }

    static IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case ScreenshotMessage:
            case RecordingMessage:
                handled = true;
                Execute(message - CommandBase);
                break;
            case QuitMessage when !ScreenRecorder.Busy:
                handled = true;
                Application.Current.Shutdown();
                break;
            case StateMessage:
                handled = true;
                return new IntPtr(StateFlags(ScreenRecorder.Busy, ScreenRecorder.Live));
        }
        return IntPtr.Zero;
    }
}
