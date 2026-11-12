namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: snapping a held pen stroke to a shape (the geometry is in ShapeRecognizer).
internal sealed partial class ScreenshotEditor
{
    /// <summary>Strokes smaller than this stay freehand when held.</summary>
    const double MinSnapSize = 12;
    /// <summary>Closed shapes grow with the pointer's distance from their center, at least this distance.</summary>
    const double MinSnapReach = 8;
    /// <summary>The highlighter levels out below this slope (about 8°).</summary>
    const double MarkerLevelSine = 0.14;

    // Pen/highlighter held still: the stroke becomes a clean shape that can still be resized afterwards.
    internal void PenHeld()
    {
        if (mode != Mode.Draw || snapped || stroke == null || (tool != Tool.Pen && tool != Tool.Marker)) return;
        var pts = StrokePoints(stroke);
        Rect b = ShapeRecognizer.Bounds(pts);
        if (Math.Max(b.Width, b.Height) < MinSnapSize) return;
        ShapeKind kind;
        if (tool == Tool.Marker)
        {
            // highlighter: straight line, nearly horizontal becomes exactly horizontal
            kind = ShapeKind.Line;
            snapShape = null;
        }
        else
        {
            snapShape = ShapeRecognizer.Recognize(pts, StrokeWidths[widthIndex], out kind);
            if (snapShape == null) return;
        }
        snapped = true;
        snapOpen = kind == ShapeKind.Line || kind == ShapeKind.Arrow;
        snapArrow = kind == ShapeKind.Arrow;
        snapStart = pts[0];
        snapCenter = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        snapReach = Math.Max(MinSnapReach, (holdPoint - snapCenter).Length);
        if (snapArrow) stroke.Fill = stroke.Stroke;
        if (snapOpen)
        {
            // start stays, end follows the mouse
            if (snapShape != null)
            {
                snapStart = snapShape is PathGeometry pg && pg.Figures.Count > 0 ? pg.Figures[0].StartPoint : ((LineGeometry)snapShape).StartPoint;
            }
            Reshape(tool == Tool.Marker || snapShape == null ? holdPoint : LineEnd(snapShape));
        }
        else stroke.Data = snapShape;
        var flash = new DoubleAnimation(0.35, 1, Motion.ShapeFlash);
        stroke.BeginAnimation(OpacityProperty, flash);
    }

    static Point LineEnd(Geometry g)
    {
        if (g is LineGeometry lg) return lg.EndPoint;
        var pg = (PathGeometry)g;
        return pg.Figures[1].StartPoint;   // arrow: tip
    }

    // After snapping: open shapes follow with their end, closed shapes grow around their center.
    void Reshape(Point p)
    {
        if (snapOpen)
        {
            Point a = snapStart;
            double ang = Math.Atan2(p.Y - a.Y, p.X - a.X) * 180 / Math.PI;
            if (tool == Tool.Marker ? Math.Abs(Math.Sin(ang * Math.PI / 180)) < MarkerLevelSine : ShapeRecognizer.NearAxis(ang, ShapeRecognizer.AxisSnapDegrees)) p = ShapeRecognizer.SnapAxis(a, p);
            stroke!.Data = snapArrow ? ShapeRecognizer.Arrow(a, p, StrokeWidths[widthIndex]) : (Geometry)new LineGeometry(a, p);
            return;
        }
        double f = Math.Max(0.1, (p - snapCenter).Length / snapReach);
        var g = snapShape!.Clone();
        g.Transform = new ScaleTransform(f, f, snapCenter.X, snapCenter.Y);
        stroke!.Data = g;
    }
}
