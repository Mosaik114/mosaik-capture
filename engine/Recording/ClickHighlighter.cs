using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Makes clicks visible: a small circle at the click position (visible in the video). The low-level
/// mouse hook lives on its own thread with its own message loop, so a busy UI thread can never
/// delay the system's mouse input; the hook only posts the click to the UI thread.
/// </summary>
internal sealed class ClickHighlighter
{
    const int PoolSize = 6;

    /// <summary>One hook thread: its id (published before anyone waits on it) and whether it should end.</summary>
    sealed class HookRun
    {
        int threadId;
        volatile bool cancelled;

        public Thread Thread { get; set; } = null!;
        public uint ThreadId
        {
            get => (uint)Volatile.Read(ref threadId);
            set => Volatile.Write(ref threadId, (int)value);
        }
        public bool Cancelled { get => cancelled; set => cancelled = value; }
    }

    readonly NativeMethods.LowLevelMouseProc hookProc;
    readonly Func<bool> shouldShow;
    readonly List<Ripple> pool = new();
    Dispatcher? dispatcher;
    HookRun? run;
    IntPtr toolbar;
    Int32Rect area;
    int nextRipple;
    volatile bool enabled;

    /// <summary>Installing the hook takes milliseconds; after this the recording goes on without click highlights.</summary>
    static readonly TimeSpan HookStartTimeout = TimeSpan.FromSeconds(1);

    /// <param name="shouldShow">Whether clicks are shown right now (recording runs and is not paused).</param>
    public ClickHighlighter(Func<bool> shouldShow)
    {
        this.shouldShow = shouldShow;
        hookProc = OnMouseEvent;   // kept in a field so the delegate is not collected
    }

    public bool Enabled { get => enabled; set => enabled = value; }

    public void Start(Int32Rect recordedArea, IntPtr toolbarWindow)
    {
        area = recordedArea;
        toolbar = toolbarWindow;
        if (run != null) return;
        dispatcher = Dispatcher.CurrentDispatcher;
        var installed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new HookRun();
        started.Thread = new Thread(() => RunHook(started, installed)) { IsBackground = true, Name = "Mosaik Capture click hook" };
        run = started;
        started.Thread.Start();
        WatchInstallAsync(started, installed.Task).LogFailures("Starting the click highlights");
    }

    // The UI thread never waits for the hook: clicks simply start arriving once it is installed.
    // A hook that fails or takes too long is given up; the recording goes on without click highlights.
    async Task WatchInstallAsync(HookRun started, Task<bool> installed)
    {
        if (await Task.WhenAny(installed, Task.Delay(HookStartTimeout)) == installed && installed.Result) return;
        ErrorLog.Write("The mouse hook for click highlights is not available; recording without them", null);
        if (run == started) Stop();
        else started.Cancelled = true;
    }

    /// <summary>
    /// Ends the hook thread. A thread that is still installing its hook (after a start timeout) sees the
    /// cancel flag right after installing and removes the hook itself, so no global hook is left behind.
    /// </summary>
    public void Stop()
    {
        var stopping = run;
        if (stopping == null) return;
        run = null;
        stopping.Cancelled = true;
        uint threadId = stopping.ThreadId;
        if (threadId != 0) NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        // No join: the (background) thread unhooks itself when it sees WM_QUIT or the cancel flag.
    }

    void RunHook(HookRun current, TaskCompletionSource<bool> installed)
    {
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // creates the message queue before Stop can post to it
        current.ThreadId = NativeMethods.GetCurrentThreadId();   // published only once the queue exists
        IntPtr hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, hookProc, NativeMethods.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            ErrorLog.Write("Installing the mouse hook for click highlights", new Win32Exception(Marshal.GetLastWin32Error()));
            installed.TrySetResult(false);
            return;
        }
        if (current.Cancelled)   // stopped while installing: Stop may have posted before the hook existed
        {
            NativeMethods.UnhookWindowsHookEx(hook);
            installed.TrySetResult(false);
            return;
        }
        installed.TrySetResult(true);
        try
        {
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally { NativeMethods.UnhookWindowsHookEx(hook); }
    }

    // Runs on the hook thread and must return quickly: only queue the click, drawing happens on the UI thread.
    IntPtr OnMouseEvent(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && enabled)
        {
            int kind = message.ToInt32();
            if (kind == NativeMethods.WM_LBUTTONDOWN || kind == NativeMethods.WM_RBUTTONDOWN)
            {
                int x = Marshal.ReadInt32(data), y = Marshal.ReadInt32(data, 4);
                bool right = kind == NativeMethods.WM_RBUTTONDOWN;
                dispatcher?.BeginInvoke(new Action(() => Show(x, y, right)));
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    void Show(int x, int y, bool right)
    {
        if (!enabled || !shouldShow()) return;
        if (x < area.X || y < area.Y || x >= area.X + area.Width || y >= area.Y + area.Height) return;
        if (toolbar != IntPtr.Zero && NativeMethods.GetWindowRect(toolbar, out RECT bar) &&
            x >= bar.left && x < bar.right && y >= bar.top && y < bar.bottom) return;   // click on the toolbar
        Ripple ripple;
        if (pool.Count < PoolSize) { ripple = new Ripple(); pool.Add(ripple); }
        else ripple = pool[nextRipple++ % pool.Count];
        ripple.Play(x, y, right);
    }

    /// <summary>One expanding circle; a few are reused round robin.</summary>
    sealed class Ripple : Window
    {
        const double Diameter = 64, Inset = 2, RingThickness = 3, StartScale = 0.3;
        static readonly Duration AnimationLength = new(TimeSpan.FromMilliseconds(480));
        static readonly DoubleCollection RightClickDashes = new(new double[] { 2, 1.5 });
        readonly IntPtr hwnd;
        readonly Shapes.Ellipse ring;
        readonly ScaleTransform grow = new(1, 1, Diameter / 2, Diameter / 2);

        public Ripple()
        {
            ToolWindows.Setup(this, UiText.Titles.Click);
            IsHitTestVisible = false;
            Width = Height = Diameter;
            ring = new Shapes.Ellipse { Width = Diameter - 2 * Inset, Height = Diameter - 2 * Inset, StrokeThickness = RingThickness, RenderTransform = grow };
            Canvas.SetLeft(ring, Inset);
            Canvas.SetTop(ring, Inset);
            ring.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
            var canvas = new Canvas();
            canvas.Children.Add(ring);
            Content = canvas;
            grow.CenterX = grow.CenterY = (Diameter - 2 * Inset) / 2;
            hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        }

        // Left: solid circle with a light fill; right: dashed.
        public void Play(int x, int y, bool right)
        {
            ring.StrokeDashArray = right ? RightClickDashes : null;
            ring.Fill = right ? null : Palette.ClickFill;
            if (!IsVisible) Show();
            int half = (int)Math.Round(Diameter * ToolWindows.Scale(hwnd) / 2);
            NativeMethods.MoveTopmost(hwnd, x - half, y - half);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(StartScale, 1, AnimationLength) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(StartScale, 1, AnimationLength) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, AnimationLength) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
    }
}
