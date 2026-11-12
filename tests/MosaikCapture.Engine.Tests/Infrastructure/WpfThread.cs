namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// A single STA thread that owns the WPF <see cref="Application"/> and its dispatcher for the whole
/// test run. WPF objects are thread-affine and the engine relies on <c>Application.Current.Dispatcher</c>
/// and on a dispatcher synchronization context, so every test that touches WPF runs its body here.
/// Nothing is ever shown: windows only get a handle (hidden) or are rendered off-screen.
/// </summary>
internal static class WpfThread
{
    static readonly Lazy<Dispatcher> dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action body) => dispatcher.Value.Invoke(body);

    public static T Run<T>(Func<T> body) => dispatcher.Value.Invoke(body);

    /// <summary>
    /// Keeps the dispatcher processing (bindings, layout, continuations, ffmpeg callbacks) until
    /// <paramref name="until"/> is true or the timeout expires. Must be called on the WPF thread.
    /// </summary>
    public static bool PumpUntil(Func<bool> until, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!until())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) return false;
            DoEvents();
            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>Processes pending dispatcher work for roughly the given time.</summary>
    public static void Pump(int milliseconds) => PumpUntil(() => false, milliseconds);

    static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    static Dispatcher Start()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? result = null;
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            result = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WPF test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return result!;
    }
}
