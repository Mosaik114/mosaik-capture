namespace MosaikCapture.Engine.Screenshot;

/// <summary>What a freehand stroke was recognized as.</summary>
internal enum ShapeKind { None, Line, Arrow, Ellipse, Rectangle, Triangle, Rhombus }

// Screenshot editor: shape recognition, smoothing and arrows (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    // Pen/highlighter held still: the stroke becomes a clean shape that can still be resized afterwards.
    internal void PenHeld()
    {
        if (mode != Mode.Draw || snapped || stroke == null || (tool != Tool.Pen && tool != Tool.Marker)) return;
        var pts = StrokePoints(stroke);
        Rect b = Bounds(pts);
        if (Math.Max(b.Width, b.Height) < 12) return;
        ShapeKind kind;
        if (tool == Tool.Marker)
        {
            // highlighter: straight line, nearly horizontal becomes exactly horizontal
            kind = ShapeKind.Line;
            snapShape = null;
        }
        else
        {
            snapShape = Recognize(pts, StrokeWidths[widthIndex], out kind);
            if (snapShape == null) return;
        }
        snapped = true;
        snapOpen = kind == ShapeKind.Line || kind == ShapeKind.Arrow;
        snapArrow = kind == ShapeKind.Arrow;
        snapStart = pts[0];
        snapCenter = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        snapReach = Math.Max(8, (holdPoint - snapCenter).Length);
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
        var flash = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(200));
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
            if (tool == Tool.Marker ? Math.Abs(Math.Sin(ang * Math.PI / 180)) < 0.14 : Near90(ang, 4)) p = SnapAxis(a, p);
            stroke!.Data = snapArrow ? Arrow(a, p, StrokeWidths[widthIndex]) : (Geometry)new LineGeometry(a, p);
            return;
        }
        double f = Math.Max(0.1, (p - snapCenter).Length / snapReach);
        var g = snapShape!.Clone();
        g.Transform = new ScaleTransform(f, f, snapCenter.X, snapCenter.Y);
        stroke!.Data = g;
    }

    static bool Near90(double deg, double tol)
    {
        double r = ((deg % 90) + 90) % 90;
        return r < tol || r > 90 - tol;
    }

    static Point SnapAxis(Point a, Point p)
    {
        return Math.Abs(p.X - a.X) >= Math.Abs(p.Y - a.Y) ? new Point(p.X, a.Y) : new Point(a.X, p.Y);
    }

    static Rect Bounds(List<Point> pts)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var q in pts) { l = Math.Min(l, q.X); t = Math.Min(t, q.Y); r = Math.Max(r, q.X); b = Math.Max(b, q.Y); }
        return new Rect(l, t, r - l, b - t);
    }

    // Splits a stroke into pieces of equal length.
    static List<Point> Resample(List<Point> pts, double step)
    {
        var o = new List<Point> { pts[0] };
        double carry = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            Point a = pts[i - 1], b = pts[i];
            double seg = (b - a).Length;
            if (seg < 1e-9) continue;
            double t = step - carry;
            while (t <= seg)
            {
                o.Add(a + (b - a) * (t / seg));
                t += step;
            }
            carry = seg - (t - step);
        }
        if ((o[o.Count - 1] - pts[pts.Count - 1]).Length > step * 0.3) o.Add(pts[pts.Count - 1]);
        return o;
    }

    static double Length(List<Point> pts)
    {
        double l = 0;
        for (int i = 1; i < pts.Count; i++) l += (pts[i] - pts[i - 1]).Length;
        return l;
    }

    static double SegDist(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double l2 = ab.LengthSquared;
        if (l2 < 1e-9) return (p - a).Length;
        double t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / l2));
        return (p - (a + ab * t)).Length;
    }

    static double PolyDist(Point p, List<Point> poly)
    {
        double d = double.MaxValue;
        for (int i = 1; i < poly.Count; i++) d = Math.Min(d, SegDist(p, poly[i - 1], poly[i]));
        return d;
    }

    // Mean distance in both directions: stroke to shape and shape to stroke.
    static double Mismatch(List<Point> stroke, List<Point> shape)
    {
        var a = Resample(shape, Math.Max(1, Length(shape) / 64));
        double s1 = 0, s2 = 0;
        foreach (var q in stroke) s1 += PolyDist(q, shape);
        foreach (var q in a) s2 += PolyDist(q, stroke);
        return (s1 / stroke.Count + s2 / a.Count) / 2;
    }

    static List<Point> Ring(params Point[] corners)
    {
        var l = new List<Point>(corners);
        l.Add(corners[0]);
        return l;
    }

    // Recognizes a line, arrow, circle/ellipse, rectangle, triangle or rhombus in a freehand stroke; null if nothing fits well.
    internal static Geometry? Recognize(List<Point> raw, double w, out ShapeKind kind)
    {
        kind = ShapeKind.None;
        if (raw.Count < 2) return null;
        double total = Length(raw);
        if (total < 16) return null;
        var pts = Resample(raw, total / 64);
        Rect b = Bounds(pts);
        double diag = Math.Max(1, Math.Sqrt(b.Width * b.Width + b.Height * b.Height));
        Point first = pts[0], last = pts[pts.Count - 1];
        // gap between start and end; drawing past the start still counts as closed
        double gap = (last - first).Length;
        int ends = Math.Max(1, pts.Count / 5);
        for (int i = 0; i < ends; i++)
        {
            gap = Math.Min(gap, (last - pts[i]).Length);
            gap = Math.Min(gap, (first - pts[pts.Count - 1 - i]).Length);
        }

        if (gap < 0.3 * diag && total > 1.8 * diag)
        {
            // closed: fit every candidate shape to the outline and take the best
            var ring = new List<Point>(pts);
            ring.Add(first);
            Point c = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            double aspect = b.Width / Math.Max(1, b.Height);
            Rect eb = b;
            if (aspect > 0.8 && aspect < 1.25)
            {
                double d = (b.Width + b.Height) / 2;
                eb = new Rect(c.X - d / 2, c.Y - d / 2, d, d);
            }
            var ell = new List<Point>();
            for (int i = 0; i <= 72; i++)
            {
                double a = i * Math.PI * 2 / 72;
                ell.Add(new Point(c.X + eb.Width / 2 * Math.Cos(a), c.Y + eb.Height / 2 * Math.Sin(a)));
            }
            var rect = Ring(b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft);
            var rhomb = Ring(new Point(c.X, b.Top), new Point(b.Right, c.Y), new Point(c.X, b.Bottom), new Point(b.Left, c.Y));
            var up = Ring(new Point(c.X, b.Top), b.BottomRight, b.BottomLeft);
            var down = Ring(b.TopLeft, b.TopRight, new Point(c.X, b.Bottom));
            // free triangle: the three points spanning the largest area
            int n = pts.Count, ia = 0, ib = 0, ic = 0;
            double best = -1;
            for (int i = 0; i < n; i += 2)
                for (int j = i + 2; j < n; j += 2)
                    for (int k = j + 2; k < n; k += 2)
                    {
                        double ar = Math.Abs(Vector.CrossProduct(pts[j] - pts[i], pts[k] - pts[i]));
                        if (ar > best) { best = ar; ia = i; ib = j; ic = k; }
                    }
            var tri = Ring(pts[ia], pts[ib], pts[ic]);

            double eEll = Mismatch(ring, ell), eRect = Mismatch(ring, rect), eRhomb = Mismatch(ring, rhomb);
            double eUp = Mismatch(ring, up), eDown = Mismatch(ring, down), eTri = Mismatch(ring, tri);
            // isosceles looks tidier; only take the free triangle if it fits clearly better
            List<Point> triBest = eUp <= eDown ? up : down;
            double eTriBest = Math.Min(eUp, eDown);
            if (eTri < eTriBest * 0.8) { triBest = tri; eTriBest = eTri; }

            double lim = 0.055 * diag;
            double min = Math.Min(eEll, Math.Min(eRect, Math.Min(eRhomb, eTriBest)));
            if (min > lim) return null;
            // a shape is traced about once; much more stroke than that is scribbling
            var win = min == eEll ? ell : min == eRect ? rect : min == eRhomb ? rhomb : triBest;
            if (total > 1.45 * Length(win)) return null;
            if (min == eEll) { kind = ShapeKind.Ellipse; return new EllipseGeometry(eb); }
            if (min == eRect) { kind = ShapeKind.Rectangle; return new RectangleGeometry(b, 6, 6); }
            kind = min == eRhomb ? ShapeKind.Rhombus : ShapeKind.Triangle;
            var poly = min == eRhomb ? rhomb : triBest;
            var fig = new PathFigure { StartPoint = poly[0], IsClosed = true, IsFilled = false };
            for (int i = 1; i < poly.Count - 1; i++) fig.Segments.Add(new LineSegment(poly[i], true));
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            return pg;
        }

        // open: straight line?
        double dev = 0, chord = (last - first).Length;
        foreach (var q in pts) dev = Math.Max(dev, SegDist(q, first, last));
        if (chord > 12 && dev < 0.07 * chord + 3)
        {
            kind = ShapeKind.Line;
            Point e = last;
            double ang = Math.Atan2(e.Y - first.Y, e.X - first.X) * 180 / Math.PI;
            if (Near90(ang, 4)) e = SnapAxis(first, e);
            return new LineGeometry(first, e);
        }

        // arrow: straight shaft up to the tip, then a short way back (a hinted barb)
        int tipAt = 0;
        double far = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double d = (pts[i] - first).Length;
            if (d > far) { far = d; tipAt = i; }
        }
        // the tip is often visited twice (there, barb, back): the first arrival counts
        for (int i = 0; i < tipAt; i++)
            if ((pts[i] - first).Length >= far * 0.97) { tipAt = i; break; }
        if (tipAt > 4 && tipAt < pts.Count - 2 && far > 24)
        {
            Point tip = pts[tipAt];
            double sdev = 0;
            for (int i = 0; i <= tipAt; i++) sdev = Math.Max(sdev, SegDist(pts[i], first, tip));
            var tail = pts.GetRange(tipAt, pts.Count - tipAt);
            double tl = Length(tail), reach = 0;
            foreach (var q in tail) reach = Math.Max(reach, (q - tip).Length);
            Vector dir = (tip - first) / far;
            double back = Vector.Multiply(last - tip, dir);
            if (sdev < 0.08 * far + 3 && tl > 0.08 * far && reach < 0.5 * far && back < -0.04 * far)
            {
                kind = ShapeKind.Arrow;
                Point t2 = tip;
                double ang = Math.Atan2(tip.Y - first.Y, tip.X - first.X) * 180 / Math.PI;
                if (Near90(ang, 4)) t2 = SnapAxis(first, tip);
                return Arrow(first, t2, w);
            }
        }
        return null;
    }

    // Smooths a freehand stroke: resample evenly, average neighbours, draw a soft curve through the midpoints.
    static Geometry? Smooth(List<Point> raw)
    {
        double total = Length(raw);
        if (raw.Count < 3 || total < 8) return null;
        var pts = Resample(raw, 3);
        if (pts.Count < 4) return null;
        var sm = new List<Point>(pts);
        for (int i = 2; i < pts.Count - 2; i++)
            sm[i] = new Point((pts[i - 2].X + pts[i - 1].X * 2 + pts[i].X * 3 + pts[i + 1].X * 2 + pts[i + 2].X) / 9,
                              (pts[i - 2].Y + pts[i - 1].Y * 2 + pts[i].Y * 3 + pts[i + 1].Y * 2 + pts[i + 2].Y) / 9);
        if (pts.Count > 3)
        {
            sm[1] = new Point((pts[0].X + pts[1].X * 2 + pts[2].X) / 4, (pts[0].Y + pts[1].Y * 2 + pts[2].Y) / 4);
            int m = pts.Count - 2;
            sm[m] = new Point((pts[m - 1].X + pts[m].X * 2 + pts[m + 1].X) / 4, (pts[m - 1].Y + pts[m].Y * 2 + pts[m + 1].Y) / 4);
        }
        var fig = new PathFigure { StartPoint = sm[0], IsFilled = false };
        var q = new PolyQuadraticBezierSegment();
        for (int i = 1; i < sm.Count - 1; i++)
        {
            q.Points.Add(sm[i]);
            q.Points.Add(new Point((sm[i].X + sm[i + 1].X) / 2, (sm[i].Y + sm[i + 1].Y) / 2));
        }
        fig.Segments.Add(q);
        fig.Segments.Add(new LineSegment(sm[sm.Count - 1], true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    static Geometry Arrow(Point a, Point b, double w)
    {
        var g = new PathGeometry();
        double len = (b - a).Length;
        if (len < 1) return g;
        double head = Math.Min(len * 0.6, 4 * w + 10), ang = Math.Atan2(b.Y - a.Y, b.X - a.X), spread = 0.42;
        var shaft = new PathFigure { StartPoint = a, IsFilled = false };
        shaft.Segments.Add(new LineSegment(new Point(b.X - head * 0.8 * Math.Cos(ang), b.Y - head * 0.8 * Math.Sin(ang)), true));
        var point = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
        point.Segments.Add(new LineSegment(new Point(b.X - head * Math.Cos(ang + spread), b.Y - head * Math.Sin(ang + spread)), true));
        point.Segments.Add(new LineSegment(new Point(b.X - head * Math.Cos(ang - spread), b.Y - head * Math.Sin(ang - spread)), true));
        g.Figures.Add(shaft);
        g.Figures.Add(point);
        return g;
    }
}
