namespace MosaikCapture.Engine.Recording;

// Frame around the recorded area (click-through, invisible in the video).
class RecFrame : Window
{
    const int Line = 2, Out = 6, Arm = 22, Thick = 4;   // device pixels
    readonly IntPtr hwnd;
    readonly Canvas canvas;
    readonly List<Shapes.Rectangle> parts = new List<Shapes.Rectangle>();

    public RecFrame()
    {
        RecUi.Setup(this, "Mosaiks-Aufnahme Rahmen");
        IsHitTestVisible = false;
        canvas = new Canvas { SnapsToDevicePixels = true };
        Content = canvas;
        hwnd = RecUi.Handle(this, RecUi.NoActivate | RecUi.Tool | RecUi.ClickThrough);
        RecUi.Exclude(hwnd);
    }

    // A line right outside the area plus bold corners; folded inwards at the monitor edge (full screen).
    public void Present(Int32Rect r, ShotOverlay.RECT mon)
    {
        Int32Rect b = Build(r, mon, RecUi.Scale(hwnd));
        if (!IsVisible) Show();
        RecUi.Bounds(hwnd, b.X, b.Y, b.Width, b.Height);
    }

    // Creates the shapes; returns the window bounds in device pixels.
    internal Int32Rect Build(Int32Rect r, ShotOverlay.RECT mon, double s)
    {
        int x0 = Math.Max(mon.left, r.X - Out), y0 = Math.Max(mon.top, r.Y - Out);
        int x1 = Math.Min(mon.right, r.X + r.Width + Out), y1 = Math.Min(mon.bottom, r.Y + r.Height + Out);
        canvas.Children.Clear();
        parts.Clear();
        // in window coordinates (device pixels)
        int rl = r.X - x0, rt = r.Y - y0, rr = rl + r.Width, rb = rt + r.Height, w = x1 - x0, h = y1 - y0;
        int ll = Math.Max(0, rl - Line), lt = Math.Max(0, rt - Line), lr = Math.Min(w, rr + Line), lb = Math.Min(h, rb + Line);
        Box(ll, lt, lr - ll, Line, s); Box(ll, lb - Line, lr - ll, Line, s);
        Box(ll, lt, Line, lb - lt, s); Box(lr - Line, lt, Line, lb - lt, s);
        int bl = Math.Max(0, rl - Out), bt = Math.Max(0, rt - Out), br = Math.Min(w, rr + Out), bb = Math.Min(h, rb + Out);
        Box(bl, bt, Arm, Thick, s); Box(bl, bt, Thick, Arm, s);
        Box(br - Arm, bt, Arm, Thick, s); Box(br - Thick, bt, Thick, Arm, s);
        Box(bl, bb - Thick, Arm, Thick, s); Box(bl, bb - Arm, Thick, Arm, s);
        Box(br - Arm, bb - Thick, Arm, Thick, s); Box(br - Thick, bb - Arm, Thick, Arm, s);
        SetPaused(false);
        Width = canvas.Width = w / s;
        Height = canvas.Height = h / s;
        return new Int32Rect(x0, y0, w, h);
    }

    void Box(int x, int y, int w, int h, double s)
    {
        var b = new Shapes.Rectangle { Width = w / s, Height = h / s };
        Canvas.SetLeft(b, x / s);
        Canvas.SetTop(b, y / s);
        canvas.Children.Add(b);
        parts.Add(b);
    }

    // Paused: frame in a muted color.
    public void SetPaused(bool paused)
    {
        foreach (var b in parts) b.SetResourceReference(Shapes.Shape.FillProperty, paused ? "Muted" : "Accent");
    }
}
