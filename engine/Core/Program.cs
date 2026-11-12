namespace MosaikCapture.Engine.Core;

/// <summary>Entry point of the capture engine and process-wide crash handling.</summary>
internal static class Program
{
    const string RestartArgument = "--idle";
    static readonly TimeSpan MinimumUptimeForRestart = TimeSpan.FromMinutes(1);

    static long startTimestamp = Stopwatch.GetTimestamp();
    static int restartAttempted;

    /// <summary>Stopwatch timestamp of the start; monotonic, so clock changes (sleep, DST, sync) do not matter.</summary>
    internal static long StartTimestamp
    {
        get => Interlocked.Read(ref startTimestamp);
        set => Interlocked.Exchange(ref startTimestamp, value);
    }

    /// <summary>A restart was attempted already (a second crash report must not start another engine).</summary>
    internal static bool RestartAttempted
    {
        get => Volatile.Read(ref restartAttempted) == 1;
        set => Volatile.Write(ref restartAttempted, value ? 1 : 0);
    }

    [STAThread]
    static void Main(string[] args) => CaptureBridge.Run(args);

    internal static void Protect(Application app)
    {
        // The flows guard their own entry points; what still arrives here escaped them. Failures of the
        // outside world (a device, the clipboard, a file) are logged and the engine goes on; anything else
        // is a bug that may have left state broken, so this engine ends (restarting first unless it just started).
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (IsRecoverable(e.Exception))
            {
                ErrorLog.Write("Unhandled UI exception (recoverable)", e.Exception);
                return;
            }
            // a bug: restart if possible, otherwise just end (the host starts the engine again when needed)
            ErrorLog.Write("Unhandled UI exception: restartStarted", e.Exception);
            if (ResponseTo(e.Exception, Restart) == UiFailureResponse.Shutdown) app.Shutdown();
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

    /// <summary>What happens after an unhandled exception on the UI thread.</summary>
    internal enum UiFailureResponse
    {
        /// <summary>A recoverable failure: log and keep running.</summary>
        Continue,
        /// <summary>A bug: end this engine. A new one runs already, or the host starts one with the next capture.</summary>
        Shutdown,
    }

    /// <summary>
    /// Recoverable failures keep the engine running; anything else ends it after trying a restart
    /// (<paramref name="restart"/>; skipped within the first minute). Never continues in a broken state.
    /// </summary>
    internal static UiFailureResponse ResponseTo(Exception exception, Func<bool> restart)
    {
        if (IsRecoverable(exception)) return UiFailureResponse.Continue;
        restart();
        return UiFailureResponse.Shutdown;
    }

    /// <summary>
    /// Failures caused by the outside world (devices, clipboard, files, Win32, timeouts, cancelled work),
    /// also when wrapped in an <see cref="AggregateException"/> or <see cref="TargetInvocationException"/>.
    /// </summary>
    internal static bool IsRecoverable(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsRecoverable),
        TargetInvocationException invocation => invocation.InnerException != null && IsRecoverable(invocation.InnerException),
        IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException => true,
        SEHException => false,       // a native crash (access violation ...): state may be corrupt
        ExternalException => true,   // COMException, Win32Exception ...
        _ => false,
    };

    /// <summary>How long this engine has been running.</summary>
    internal static TimeSpan Uptime => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - StartTimestamp) / (double)Stopwatch.Frequency);

    /// <summary>
    /// Restarts only if the engine has been running for a while; otherwise a crash at startup would loop.
    /// Returns whether a new engine was started (then this one should end).
    /// </summary>
    internal static bool Restart()
    {
        if (Interlocked.Exchange(ref restartAttempted, 1) == 1) return false;
        if (Uptime < MinimumUptimeForRestart)
        {
            ErrorLog.Write("No restart: crashed within the first minute", null);
            return false;
        }
        // The new engine waits for this process to be gone before it takes the single-instance mutex.
        return Shell.Open(Assembly.GetEntryAssembly()!.Location, RestartArguments(CurrentProcessId()));
    }
}
