namespace MosaikCapture.Engine.Core;

/// <summary>Entry point of the capture engine and process-wide crash handling.</summary>
internal static class Program
{
    const string RestartArgument = "--idle";
    static readonly TimeSpan MinimumUptimeForRestart = TimeSpan.FromMinutes(1);

    internal static DateTime started = DateTime.Now;
    internal static int restarting;

    [STAThread]
    static void Main(string[] args) => CaptureBridge.Run(args);

    internal static void Protect(Application app)
    {
        // A single failure (e.g. after standby, when a device is missing) is only logged
        // and must not take down the whole engine.
        app.DispatcherUnhandledException += (_, e) => { ErrorLog.Write("Unhandled UI exception", e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write("Unobserved task exception", e.Exception); e.SetObserved(); };
        // Not recoverable (failure outside the UI thread): log it and restart fresh.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ErrorLog.Write("Crash", e.ExceptionObject as Exception);
            if (e.IsTerminating) Restart();
        };
    }

    /// <summary>Restarts only if the engine has been running for a while; otherwise a crash at startup would loop.</summary>
    internal static void Restart()
    {
        if (Interlocked.Exchange(ref restarting, 1) == 1) return;
        if (DateTime.Now - started < MinimumUptimeForRestart)
        {
            ErrorLog.Write("No restart: crashed within the first minute", null);
            return;
        }
        Shell.Open(Assembly.GetEntryAssembly()!.Location, RestartArgument);
    }
}
