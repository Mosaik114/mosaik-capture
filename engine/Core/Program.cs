namespace MosaikCapture.Engine.Core;

// Entry point of the capture engine and process-wide crash handling.
static class Program
{
    const string RestartArg = "--idle";
    internal static DateTime started = DateTime.Now;
    internal static int restarting;

    [STAThread]
    static void Main(string[] args) => CaptureBridge.Run(args);

    internal static void Protect(Application app)
    {
        // A single failure (e.g. after standby, when a device is missing) is only logged
        // and must not take down the whole engine.
        app.DispatcherUnhandledException += (s, e) => { ErrorLog.Write(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (s, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };
        // Not recoverable (failure outside the UI thread): log it and restart fresh.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            ErrorLog.Write("Absturz", e.ExceptionObject as Exception);
            if (e.IsTerminating) Restart();
        };
    }

    // Only restarts if the engine has been running for a while; otherwise a crash at startup would loop.
    internal static void Restart()
    {
        if (Interlocked.Exchange(ref restarting, 1) == 1) return;
        if (DateTime.Now - started < TimeSpan.FromMinutes(1)) { ErrorLog.Write("Kein Neustart: Absturz in der ersten Minute", null); return; }
        Shell.Open(Assembly.GetEntryAssembly()!.Location, RestartArg);
    }
}
