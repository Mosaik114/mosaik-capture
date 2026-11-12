namespace MosaikCapture.Engine.Recording;

/// <summary>Frame around the recorded area (click-through, invisible in the video).</summary>
internal sealed class RecordingFrame : Window
{
    // device pixels
    const int LineWidth = 2, FrameMargin = 6, CornerArm = 22, CornerThickness = 4;
    readonly IntPtr hwnd;
    readonly Canvas canvas;
    readonly List<Shapes.Rectangle> parts = new();

    public RecordingFrame()
    {
        ToolWindows.Setup(this, UiText.Titles.RecordingFrame);
        IsHitTestVisible = false;
        canvas = new Canvas { SnapsToDevicePixels = true };
        Content = canvas;
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        ToolWindows.ExcludeFromCapture(hwnd);
    }

    /// <summary>A line right outside the area plus bold corners; folded inwards at the monitor edge (full screen).</summary>
    public void Present(Int32Rect area, RECT monitor)
    {
        Int32Rect bounds = Build(area, monitor, ToolWindows.Scale(hwnd));
        if (!IsVisible) Show();
        NativeMethods.PlaceTopmost(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Creates the shapes for <paramref name="scale"/> device pixels per WPF unit; returns the window bounds in device pixels.</summary>
    internal Int32Rect Build(Int32Rect area, RECT monitor, double scale)
    {
        int windowLeft = Math.Max(monitor.left, area.X - FrameMargin), windowTop = Math.Max(monitor.top, area.Y - FrameMargin);
        int windowRight = Math.Min(monitor.right, area.X + area.Width + FrameMargin), windowBottom = Math.Min(monitor.bottom, area.Y + area.Height + FrameMargin);
        int width = windowRight - windowLeft, height = windowBottom - windowTop;
        canvas.Children.Clear();
        parts.Clear();

        // the area in window coordinates (device pixels)
        int left = area.X - windowLeft, top = area.Y - windowTop, right = left + area.Width, bottom = top + area.Height;

        int lineLeft = Math.Max(0, left - LineWidth), lineTop = Math.Max(0, top - LineWidth);
        int lineRight = Math.Min(width, right + LineWidth), lineBottom = Math.Min(height, bottom + LineWidth);
        AddBar(lineLeft, lineTop, lineRight - lineLeft, LineWidth, scale);
        AddBar(lineLeft, lineBottom - LineWidth, lineRight - lineLeft, LineWidth, scale);
        AddBar(lineLeft, lineTop, LineWidth, lineBottom - lineTop, scale);
        AddBar(lineRight - LineWidth, lineTop, LineWidth, lineBottom - lineTop, scale);

        int outerLeft = Math.Max(0, left - FrameMargin), outerTop = Math.Max(0, top - FrameMargin);
        int outerRight = Math.Min(width, right + FrameMargin), outerBottom = Math.Min(height, bottom + FrameMargin);
        AddCorner(outerLeft, outerTop, 1, 1, scale);
        AddCorner(outerRight, outerTop, -1, 1, scale);
        AddCorner(outerLeft, outerBottom, 1, -1, scale);
        AddCorner(outerRight, outerBottom, -1, -1, scale);

        SetPaused(false);
        Width = canvas.Width = width / scale;
        Height = canvas.Height = height / scale;
        return new Int32Rect(windowLeft, windowTop, width, height);
    }

    // An L-shaped corner at (x, y) with its arms pointing right/down (+1) or left/up (-1).
    void AddCorner(int x, int y, int horizontal, int vertical, double scale)
    {
        AddBar(horizontal > 0 ? x : x - CornerArm, vertical > 0 ? y : y - CornerThickness, CornerArm, CornerThickness, scale);
        AddBar(horizontal > 0 ? x : x - CornerThickness, vertical > 0 ? y : y - CornerArm, CornerThickness, CornerArm, scale);
    }

    void AddBar(int x, int y, int width, int height, double scale)
    {
        var bar = new Shapes.Rectangle { Width = width / scale, Height = height / scale };
        Canvas.SetLeft(bar, x / scale);
        Canvas.SetTop(bar, y / scale);
        canvas.Children.Add(bar);
        parts.Add(bar);
    }

    /// <summary>Paused: the frame in a muted color.</summary>
    public void SetPaused(bool paused)
    {
        foreach (var part in parts) part.SetResourceReference(Shapes.Shape.FillProperty, paused ? Palette.Muted : Palette.Accent);
    }
}
