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
        // The flows guard their own entry points; what still arrives here escaped them. Failures of the
        // outside world (a device, the clipboard, a file) are logged and the engine goes on; anything else
        // is a bug that may have left state broken, so the engine restarts fresh (unless it just started).
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (IsRecoverable(e.Exception))
            {
                ErrorLog.Write("Unhandled UI exception (recoverable)", e.Exception);
                return;
            }
            ErrorLog.Write("Unhandled UI exception: restarting", e.Exception);
            if (Restart()) app.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write("Unobserved task exception", e.Exception); e.SetObserved(); };
        // Not recoverable (failure outside the UI thread): log it and restart fresh.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ErrorLog.Write("Crash", e.ExceptionObject as Exception);
            if (e.IsTerminating) Restart();
        };
    }

    static int CurrentProcessId()
    {
        using var self = Process.GetCurrentProcess();
        return self.Id;
    }

    internal static string RestartArguments(int pid) => RestartArgument + " " + CaptureBridge.AfterArgument + " " + pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Failures caused by the outside world (devices, clipboard, files, Win32, timeouts, cancelled work),
    /// also when wrapped in an <see cref="AggregateException"/> or <see cref="TargetInvocationException"/>.
    /// </summary>
    internal static bool IsRecoverable(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsRecoverable),
        TargetInvocationException invocation => invocation.InnerException != null && IsRecoverable(invocation.InnerException),
        IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException => true,
        ExternalException => true,   // COMException, Win32Exception, SEHException ...
        _ => false,
    };

    /// <summary>
    /// Restarts only if the engine has been running for a while; otherwise a crash at startup would loop.
    /// Returns whether a new engine was started (then this one should end).
    /// </summary>
    internal static bool Restart()
    {
        if (Interlocked.Exchange(ref restarting, 1) == 1) return false;
        if (DateTime.Now - started < MinimumUptimeForRestart)
        {
            ErrorLog.Write("No restart: crashed within the first minute", null);
            return false;
        }
        // The new engine waits for this process to be gone before it takes the single-instance mutex.
        return Shell.Open(Assembly.GetEntryAssembly()!.Location, RestartArguments(CurrentProcessId()));
    }
}
