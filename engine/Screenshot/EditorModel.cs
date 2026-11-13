namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Undo and redo of the screenshot editor: every change is a pair of actions. Plain model, no UI;
/// the editor commits a pending text before replaying.
/// </summary>
internal sealed class UndoHistory
{
    readonly List<(Action Undo, Action Redo)> undo = new(), redo = new();

    /// <summary>True once anything was changed (Esc then asks before discarding).</summary>
    public bool HasChanges => undo.Count > 0;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Records a change that was just made; a new change clears the redo list.</summary>
    public void Push(Action undoAction, Action redoAction)
    {
        undo.Add((undoAction, redoAction));
        redo.Clear();
    }

    /// <summary>Reverts the latest change; false if there is none.</summary>
    public bool Undo() => Replay(undo, redo, step => step.Undo());

    /// <summary>Repeats the latest undone change; false if there is none.</summary>
    public bool Redo() => Replay(redo, undo, step => step.Redo());

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }

    static bool Replay(List<(Action Undo, Action Redo)> from, List<(Action Undo, Action Redo)> to, Action<(Action Undo, Action Redo)> apply)
    {
        if (from.Count == 0) return false;
        var step = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);
        apply(step);
        to.Add(step);
        return true;
    }
}

/// <summary>Color and size step of new markup, and the sizes each tool uses at that step. Plain model, no UI.</summary>
internal sealed class InkStyle
{
    /// <summary>The six ink colors in toolbar order (keys 1-6).</summary>
    public static readonly Color[] Colors =
    {
        Color.FromRgb(0x0E, 0x8C, 0x80), Color.FromRgb(0xF0, 0x80, 0x6A), Color.FromRgb(0xF2, 0xC9, 0x4C),
        Color.FromRgb(0x6F, 0xBF, 0x8E), Color.FromRgb(0x6F, 0xA8, 0xDC), Color.FromRgb(0xFA, 0xF9, 0xF5)
    };

    // per size step (very fine .. very thick)
    static readonly double[] StrokeWidths = { 2, 3, 5, 8, 12 }, MarkerWidths = { 10, 14, 22, 32, 44 }, TextSizes = { 16, 20, 26, 34, 44 }, BadgeSizes = { 20, 24, 30, 38, 48 };

    /// <summary>Number of size steps.</summary>
    public static int Steps => StrokeWidths.Length;

    /// <summary>Highlighter ink is the color at this opacity.</summary>
    const byte MarkerAlpha = 0x66;

    public int ColorIndex { get; private set; }
    public int WidthIndex { get; private set; } = 2;

    public Color Ink => Colors[ColorIndex];
    public Color MarkerInk => Color.FromArgb(MarkerAlpha, Ink.R, Ink.G, Ink.B);
    public double StrokeWidth => StrokeWidths[WidthIndex];
    public double MarkerWidth => MarkerWidths[WidthIndex];
    public double TextSize => TextSizes[WidthIndex];
    public double BadgeSize => BadgeSizes[WidthIndex];

    public void SetColor(int index) => ColorIndex = Math.Max(0, Math.Min(Colors.Length - 1, index));

    public void SetWidth(int index) => WidthIndex = Math.Max(0, Math.Min(Steps - 1, index));

    /// <summary>One size step up or down; false at either end.</summary>
    public bool StepWidth(int direction)
    {
        int index = Math.Max(0, Math.Min(Steps - 1, WidthIndex + direction));
        if (index == WidthIndex) return false;
        WidthIndex = index;
        return true;
    }
}

/// <summary>Geometry of the crop frame, its handles and the toolbar placement (WPF units). Pure, no UI.</summary>
internal static class CropGeometry
{
    /// <summary>The crop never gets smaller than this.</summary>
    public const double MinSize = 8;

    // Resize handles clockwise from the top-left corner, as fractions of the crop.
    static readonly double[] HandleX = { 0, 0.5, 1, 1, 1, 0.5, 0, 0 }, HandleY = { 0, 0, 0, 0.5, 1, 1, 1, 0.5 };

    public static int HandleCount => HandleX.Length;

    /// <summary>Center of resize handle <paramref name="handle"/>.</summary>
    public static Point HandleCenter(Rect crop, int handle) => new(crop.X + crop.Width * HandleX[handle], crop.Y + crop.Height * HandleY[handle]);

    /// <summary>Rounds to whole device pixels.</summary>
    public static double SnapToPixel(double value, double scale) => Math.Round(value * scale) / scale;

    /// <summary>The crop moved by the pointer since the press, kept on the surface.</summary>
    public static Rect Moved(Rect atPress, Vector delta, Size surface, double scale)
    {
        double x = Math.Max(0, Math.Min(surface.Width - atPress.Width, atPress.X + delta.X));
        double y = Math.Max(0, Math.Min(surface.Height - atPress.Height, atPress.Y + delta.Y));
        return new Rect(SnapToPixel(x, scale), SnapToPixel(y, scale), atPress.Width, atPress.Height);
    }

    /// <summary>The crop with handle <paramref name="handle"/> dragged to <paramref name="pointer"/>; never below <see cref="MinSize"/>.</summary>
    public static Rect Resized(Rect atPress, int handle, Point pointer, Size surface, double scale)
    {
        double left = atPress.Left, top = atPress.Top, right = atPress.Right, bottom = atPress.Bottom;
        double x = Math.Max(0, Math.Min(surface.Width, pointer.X)), y = Math.Max(0, Math.Min(surface.Height, pointer.Y));
        if (HandleX[handle] == 0) left = Math.Min(x, right - MinSize);
        else if (HandleX[handle] == 1) right = Math.Max(x, left + MinSize);
        if (HandleY[handle] == 0) top = Math.Min(y, bottom - MinSize);
        else if (HandleY[handle] == 1) bottom = Math.Max(y, top + MinSize);
        return new Rect(SnapToPixel(left, scale), SnapToPixel(top, scale),
            SnapToPixel(right, scale) - SnapToPixel(left, scale), SnapToPixel(bottom, scale) - SnapToPixel(top, scale));
    }

    /// <summary>The crop in device pixels of the screenshot, at least 1 x 1 and inside the image.</summary>
    public static Int32Rect InPixels(Rect crop, double scale, int imageWidth, int imageHeight)
    {
        int x = (int)Math.Round(crop.X * scale), y = (int)Math.Round(crop.Y * scale);
        int width = Math.Min((int)Math.Round(crop.Width * scale), imageWidth - x);
        int height = Math.Min((int)Math.Round(crop.Height * scale), imageHeight - y);
        return new Int32Rect(x, y, Math.Max(1, width), Math.Max(1, height));
    }

    public static Point KeepInside(Rect area, Point point) =>
        new(Math.Max(area.Left, Math.Min(area.Right, point.X)), Math.Max(area.Top, Math.Min(area.Bottom, point.Y)));

    /// <summary>Same length, direction rounded to 45 degree steps, kept in <paramref name="area"/>.</summary>
    public static Point SnapTo45Degrees(Point start, Point end, Rect area)
    {
        double angle = Math.Round(Math.Atan2(end.Y - start.Y, end.X - start.X) / (Math.PI / 4)) * (Math.PI / 4), length = (end - start).Length;
        return KeepInside(area, new Point(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle)));
    }

    /// <summary>The corner of a square with the larger side, kept in <paramref name="area"/>.</summary>
    public static Point SquareCorner(Point start, Point end, Rect area)
    {
        double width = end.X - start.X, height = end.Y - start.Y, side = Math.Max(Math.Abs(width), Math.Abs(height));
        return KeepInside(area, new Point(start.X + (width < 0 ? -side : side), start.Y + (height < 0 ? -side : side)));
    }

    // Toolbar placement: gap to the crop, margin to the monitor edge and room needed below it.
    const double ToolbarGap = 14, EdgeMargin = 12, RoomBelow = 50, InsideOffset = 24;
    const double ChipAbove = 34, ChipBelow = 10, ChipMargin = 4;

    /// <summary>Below the crop; no room below: above, otherwise inside. Always on the monitor.</summary>
    public static Point ToolbarPosition(Rect crop, Size toolbar, Rect monitor)
    {
        double x = Math.Max(monitor.Left + EdgeMargin, Math.Min(monitor.Right - EdgeMargin - toolbar.Width, crop.X + (crop.Width - toolbar.Width) / 2));
        double y = crop.Bottom + ToolbarGap;
        if (y + toolbar.Height + RoomBelow > monitor.Bottom)
            y = crop.Top - ToolbarGap - toolbar.Height >= monitor.Top + RoomBelow ? crop.Top - ToolbarGap - toolbar.Height : crop.Bottom - toolbar.Height - InsideOffset;
        y = Math.Max(monitor.Top + EdgeMargin, Math.Min(monitor.Bottom - toolbar.Height - EdgeMargin, y));
        return new Point(x, y);
    }

    /// <summary>Top of the size chip: above the crop if there is room on the monitor, otherwise inside it.</summary>
    public static double SizeChipTop(Rect crop, Rect monitor) => crop.Y - ChipAbove >= monitor.Top + ChipMargin ? crop.Y - ChipAbove : crop.Y + ChipBelow;
}
