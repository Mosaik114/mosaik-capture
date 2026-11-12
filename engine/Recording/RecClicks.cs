namespace MosaikCapture.Engine.Recording;

// Visualizes clicks: a small circle at the click position (visible in the video).
class RecClicks
{
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    readonly HookProc proc;
    readonly List<Ripple> pool = new List<Ripple>();
    IntPtr hook, bar;
    Int32Rect area;
    int next;
    public bool Enabled;

    public RecClicks() { proc = Proc; }

    public void Start(Int32Rect a, IntPtr barHandle)
    {
        area = a;
        bar = barHandle;
        if (hook == IntPtr.Zero) hook = SetWindowsHookEx(14, proc, GetModuleHandle(null), 0);   // WH_MOUSE_LL
    }

    public void Stop()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    // Must return quickly: only queue the click, drawing happens later.
    IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && Enabled)
        {
            int m = w.ToInt32();
            if (m == 0x201 || m == 0x204)   // left / right button down
            {
                int x = Marshal.ReadInt32(l), y = Marshal.ReadInt32(l, 4);
                bool right = m == 0x204;
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate { Show(x, y, right); }));
            }
        }
        return CallNextHookEx(hook, code, w, l);
    }

    void Show(int x, int y, bool right)
    {
        if (!Enabled || Rec.Paused || !Rec.Live) return;
        if (x < area.X || y < area.Y || x >= area.X + area.Width || y >= area.Y + area.Height) return;
        if (bar != IntPtr.Zero && GetWindowRect(bar, out RECT b) && x >= b.left && x < b.right && y >= b.top && y < b.bottom) return;   // click on the toolbar
        Ripple r;
        if (pool.Count < 6) { r = new Ripple(); pool.Add(r); }
        else r = pool[next++ % pool.Count];
        r.Play(x, y, right);
    }

    class Ripple : Window
    {
        const double Size = 64;
        readonly IntPtr hwnd;
        readonly Shapes.Ellipse ring;
        readonly ScaleTransform grow = new ScaleTransform(1, 1, Size / 2, Size / 2);

        public Ripple()
        {
            RecUi.Setup(this, "Mosaiks-Aufnahme Klick");
            IsHitTestVisible = false;
            Width = Height = Size;
            ring = new Shapes.Ellipse { Width = Size - 4, Height = Size - 4, StrokeThickness = 3, RenderTransform = grow };
            Canvas.SetLeft(ring, 2);
            Canvas.SetTop(ring, 2);
            ring.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
            var c = new Canvas();
            c.Children.Add(ring);
            Content = c;
            grow.CenterX = grow.CenterY = (Size - 4) / 2;
            hwnd = RecUi.Handle(this, RecUi.NoActivate | RecUi.Tool | RecUi.ClickThrough);
        }

        // Left: solid circle with a light fill; right: dashed.
        public void Play(int x, int y, bool right)
        {
            ring.StrokeDashArray = right ? new DoubleCollection(new double[] { 2, 1.5 }) : null;
            ring.Fill = right ? null : ShotOverlay.Frozen(0x40, 0xD9, 0x77, 0x57);
            if (!IsVisible) Show();
            double s = RecUi.Scale(hwnd);
            int half = (int)Math.Round(Size * s / 2);
            RecUi.Move(hwnd, x - half, y - half);
            var d = new Duration(TimeSpan.FromMilliseconds(480));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, d) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, d) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
}
