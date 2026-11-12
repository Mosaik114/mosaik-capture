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
    readonly NativeMethods.LowLevelMouseProc hookProc;
    readonly List<Ripple> pool = new();
    Dispatcher? dispatcher;
    Thread? hookThread;
    uint hookThreadId;
    IntPtr toolbar;
    Int32Rect area;
    int nextRipple;
    volatile bool enabled;

    public ClickHighlighter() => hookProc = OnMouseEvent;   // kept in a field so the delegate is not collected

    public bool Enabled { get => enabled; set => enabled = value; }

    public void Start(Int32Rect recordedArea, IntPtr toolbarWindow)
    {
        area = recordedArea;
        toolbar = toolbarWindow;
        if (hookThread != null) return;
        dispatcher = Dispatcher.CurrentDispatcher;
        using var ready = new ManualResetEventSlim();
        hookThread = new Thread(() => RunHook(ready)) { IsBackground = true, Name = "Mosaik Capture click hook" };
        hookThread.Start();
        ready.Wait();   // installing the hook takes milliseconds
    }

    public void Stop()
    {
        var thread = hookThread;
        if (thread == null) return;
        hookThread = null;
        NativeMethods.PostThreadMessage(hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    void RunHook(ManualResetEventSlim ready)
    {
        hookThreadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // creates the message queue before Stop can post to it
        IntPtr hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, hookProc, NativeMethods.GetModuleHandle(null), 0);
        ready.Set();
        if (hook == IntPtr.Zero)
        {
            ErrorLog.Write("Installing the mouse hook for click highlights", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
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
        if (!enabled || ScreenRecorder.Paused || !ScreenRecorder.Live) return;
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
        const double Diameter = 64;
        readonly IntPtr hwnd;
        readonly Shapes.Ellipse ring;
        readonly ScaleTransform grow = new(1, 1, Diameter / 2, Diameter / 2);

        public Ripple()
        {
            ToolWindows.Setup(this, UiText.Titles.Click);
            IsHitTestVisible = false;
            Width = Height = Diameter;
            ring = new Shapes.Ellipse { Width = Diameter - 4, Height = Diameter - 4, StrokeThickness = 3, RenderTransform = grow };
            Canvas.SetLeft(ring, 2);
            Canvas.SetTop(ring, 2);
            ring.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
            var canvas = new Canvas();
            canvas.Children.Add(ring);
            Content = canvas;
            grow.CenterX = grow.CenterY = (Diameter - 4) / 2;
            hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        }

        // Left: solid circle with a light fill; right: dashed.
        public void Play(int x, int y, bool right)
        {
            ring.StrokeDashArray = right ? new DoubleCollection(new double[] { 2, 1.5 }) : null;
            ring.Fill = right ? null : Palette.Frozen(0x40, 0xD9, 0x77, 0x57);
            if (!IsVisible) Show();
            int half = (int)Math.Round(Diameter * ToolWindows.Scale(hwnd) / 2);
            NativeMethods.MoveTopmost(hwnd, x - half, y - half);
            var duration = new Duration(TimeSpan.FromMilliseconds(480));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, duration) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, duration) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
    }
}
