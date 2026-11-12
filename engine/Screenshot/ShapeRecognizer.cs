namespace MosaikCapture.Engine.Screenshot;

/// <summary>What a freehand stroke was recognized as.</summary>
internal enum ShapeKind { None, Line, Arrow, Ellipse, Rectangle, Triangle, Rhombus }

/// <summary>
/// Pure geometry for the editor's pen: turns a hand-drawn stroke into a clean line, arrow, circle/ellipse,
/// rectangle, triangle or rhombus, and smooths strokes that stay freehand. No UI state; all members are thread-safe.
/// </summary>
internal static class ShapeRecognizer
{
    /// <summary>Points per stroke after resampling, and per ellipse outline.</summary>
    const int Samples = 64, EllipseSteps = 72;
    /// <summary>Strokes shorter than this are never shapes.</summary>
    const double MinStrokeLength = 16;
    /// <summary>Start and end closer than this share of the diagonal count as a closed outline...</summary>
    const double ClosedGap = 0.3;
    /// <summary>...provided the stroke is at least this long relative to the diagonal.</summary>
    const double ClosedLength = 1.8;
    /// <summary>Width/height ratio within which an ellipse becomes a perfect circle.</summary>
    const double CircleAspectMin = 0.8, CircleAspectMax = 1.25;
    /// <summary>Largest mean distance between stroke and shape, relative to the diagonal.</summary>
    const double FitTolerance = 0.055;
    /// <summary>A shape is traced about once; more stroke than this is scribbling.</summary>
    const double MaxTraceFactor = 1.45;
    /// <summary>The free triangle must fit this much better than an isosceles one to win.</summary>
    const double FreeTriangleAdvantage = 0.8;
    /// <summary>Angles within this many degrees of an axis snap onto it.</summary>
    internal const double AxisSnapDegrees = 4;
    const double RectangleCorner = 6;

    /// <summary>Recognizes a shape in a freehand stroke; null (and <see cref="ShapeKind.None"/>) if nothing fits well.</summary>
    /// <param name="raw">The stroke as drawn.</param>
    /// <param name="width">Stroke width (arrow heads scale with it).</param>
    /// <param name="kind">What was recognized.</param>
    public static Geometry? Recognize(List<Point> raw, double width, out ShapeKind kind)
    {
        kind = ShapeKind.None;
        if (raw.Count < 2) return null;
        double total = Length(raw);
        if (total < MinStrokeLength) return null;
        var points = Resample(raw, total / Samples);
        Rect bounds = Bounds(points);
        double diagonal = Math.Max(1, Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height));
        if (IsClosed(points, diagonal, total)) return RecognizeClosed(points, bounds, diagonal, total, out kind);
        return RecognizeLine(points, out kind) ?? RecognizeArrow(points, width, out kind);
    }

    // ---------- Closed outlines ----------

    // Gap between start and end; drawing past the start still counts as closed.
    static bool IsClosed(List<Point> points, double diagonal, double total)
    {
        Point first = points[0], last = points[points.Count - 1];
        double gap = (last - first).Length;
        int ends = Math.Max(1, points.Count / 5);
        for (int i = 0; i < ends; i++)
        {
            gap = Math.Min(gap, (last - points[i]).Length);
            gap = Math.Min(gap, (first - points[points.Count - 1 - i]).Length);
        }
        return gap < ClosedGap * diagonal && total > ClosedLength * diagonal;
    }

    /// <summary>A candidate outline and how far the stroke is from it.</summary>
    sealed class Candidate
    {
        public Candidate(ShapeKind kind, List<Point> outline, double mismatch)
        {
            Kind = kind;
            Outline = outline;
            Mismatch = mismatch;
        }

        public ShapeKind Kind { get; }
        public List<Point> Outline { get; }
        public double Mismatch { get; }
    }

    // Fits every candidate shape to the outline and takes the best.
    static Geometry? RecognizeClosed(List<Point> points, Rect bounds, double diagonal, double total, out ShapeKind kind)
    {
        kind = ShapeKind.None;
        var ring = new List<Point>(points) { points[0] };
        Rect ellipseBounds = EllipseBounds(bounds);
        var candidates = new[]
        {
            new Candidate(ShapeKind.Ellipse, EllipseOutline(ellipseBounds), 0),
            new Candidate(ShapeKind.Rectangle, Ring(bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft), 0),
            new Candidate(ShapeKind.Rhombus, RhombusOutline(bounds), 0),
            BestTriangle(ring, points, bounds),
        }.Select(c => c.Kind == ShapeKind.Triangle ? c : new Candidate(c.Kind, c.Outline, Mismatch(ring, c.Outline))).ToList();

        // ties keep the order above (ellipse first), as the comparison chain always did
        var best = candidates.Aggregate((a, b) => b.Mismatch < a.Mismatch ? b : a);
        if (best.Mismatch > FitTolerance * diagonal) return null;
        if (total > MaxTraceFactor * Length(best.Outline)) return null;
        kind = best.Kind;
        return kind switch
        {
            ShapeKind.Ellipse => new EllipseGeometry(ellipseBounds),
            ShapeKind.Rectangle => new RectangleGeometry(bounds, RectangleCorner, RectangleCorner),
            _ => ClosedPath(best.Outline),
        };
    }

    // Nearly square bounds become a circle.
    static Rect EllipseBounds(Rect bounds)
    {
        double aspect = bounds.Width / Math.Max(1, bounds.Height);
        if (aspect <= CircleAspectMin || aspect >= CircleAspectMax) return bounds;
        Point center = Center(bounds);
        double diameter = (bounds.Width + bounds.Height) / 2;
        return new Rect(center.X - diameter / 2, center.Y - diameter / 2, diameter, diameter);
    }

    static List<Point> EllipseOutline(Rect bounds)
    {
        Point center = Center(bounds);
        var outline = new List<Point>();
        for (int i = 0; i <= EllipseSteps; i++)
        {
            double angle = i * Math.PI * 2 / EllipseSteps;
            outline.Add(new Point(center.X + bounds.Width / 2 * Math.Cos(angle), center.Y + bounds.Height / 2 * Math.Sin(angle)));
        }
        return outline;
    }

    static List<Point> RhombusOutline(Rect bounds)
    {
        Point center = Center(bounds);
        return Ring(new Point(center.X, bounds.Top), new Point(bounds.Right, center.Y), new Point(center.X, bounds.Bottom), new Point(bounds.Left, center.Y));
    }

    // Isosceles (tip up or down) looks tidier; the free triangle only wins if it fits clearly better.
    static Candidate BestTriangle(List<Point> ring, List<Point> points, Rect bounds)
    {
        Point center = Center(bounds);
        var up = Ring(new Point(center.X, bounds.Top), bounds.BottomRight, bounds.BottomLeft);
        var down = Ring(bounds.TopLeft, bounds.TopRight, new Point(center.X, bounds.Bottom));
        var free = LargestTriangle(points);
        double upMismatch = Mismatch(ring, up), downMismatch = Mismatch(ring, down), freeMismatch = Mismatch(ring, free);
        var best = upMismatch <= downMismatch ? new Candidate(ShapeKind.Triangle, up, upMismatch) : new Candidate(ShapeKind.Triangle, down, downMismatch);
        return freeMismatch < best.Mismatch * FreeTriangleAdvantage ? new Candidate(ShapeKind.Triangle, free, freeMismatch) : best;
    }

    // The three stroke points spanning the largest area.
    static List<Point> LargestTriangle(List<Point> points)
    {
        int n = points.Count, a = 0, b = 0, c = 0;
        double largest = -1;
        for (int i = 0; i < n; i += 2)
            for (int j = i + 2; j < n; j += 2)
                for (int k = j + 2; k < n; k += 2)
                {
                    double area = Math.Abs(Vector.CrossProduct(points[j] - points[i], points[k] - points[i]));
                    if (area > largest) { largest = area; a = i; b = j; c = k; }
                }
        return Ring(points[a], points[b], points[c]);
    }

    static PathGeometry ClosedPath(List<Point> outline)
    {
        var figure = new PathFigure { StartPoint = outline[0], IsClosed = true, IsFilled = false };
        for (int i = 1; i < outline.Count - 1; i++) figure.Segments.Add(new LineSegment(outline[i], true));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    // ---------- Open strokes ----------

    /// <summary>Largest deviation from the chord for a straight line, relative to its length plus pixels.</summary>
    const double LineDeviation = 0.07, LineDeviationPixels = 3, MinLineLength = 12;

    static Geometry? RecognizeLine(List<Point> points, out ShapeKind kind)
    {
        kind = ShapeKind.None;
        Point first = points[0], last = points[points.Count - 1];
        double deviation = 0, chord = (last - first).Length;
        foreach (var point in points) deviation = Math.Max(deviation, SegmentDistance(point, first, last));
        if (chord <= MinLineLength || deviation >= LineDeviation * chord + LineDeviationPixels) return null;
        kind = ShapeKind.Line;
        return new LineGeometry(first, SnapNearAxis(first, last));
    }

    /// <summary>Arrow: shaft straightness, barb length, how far the barb may reach and must come back (shares of the shaft).</summary>
    const double ShaftDeviation = 0.08, MinBarb = 0.08, MaxBarbReach = 0.5, MinBarbReturn = 0.04, MinShaft = 24, TipRevisit = 0.97;

    // Straight shaft up to the tip, then a short way back (a hinted barb).
    static Geometry? RecognizeArrow(List<Point> points, double width, out ShapeKind kind)
    {
        kind = ShapeKind.None;
        Point first = points[0], last = points[points.Count - 1];
        int tipAt = TipIndex(points, out double shaft);
        if (tipAt <= 4 || tipAt >= points.Count - 2 || shaft <= MinShaft) return null;
        Point tip = points[tipAt];
        double shaftDeviation = 0;
        for (int i = 0; i <= tipAt; i++) shaftDeviation = Math.Max(shaftDeviation, SegmentDistance(points[i], first, tip));
        var barb = points.GetRange(tipAt, points.Count - tipAt);
        double barbLength = Length(barb), reach = 0;
        foreach (var point in barb) reach = Math.Max(reach, (point - tip).Length);
        double back = Vector.Multiply(last - tip, (tip - first) / shaft);
        bool isArrow = shaftDeviation < ShaftDeviation * shaft + LineDeviationPixels && barbLength > MinBarb * shaft &&
            reach < MaxBarbReach * shaft && back < -MinBarbReturn * shaft;
        if (!isArrow) return null;
        kind = ShapeKind.Arrow;
        return Arrow(first, SnapNearAxis(first, tip), width);
    }

    // The point farthest from the start; it is often visited twice (there, barb, back): the first arrival counts.
    static int TipIndex(List<Point> points, out double distance)
    {
        Point first = points[0];
        int tipAt = 0;
        distance = 0;
        for (int i = 0; i < points.Count; i++)
        {
            double d = (points[i] - first).Length;
            if (d > distance) { distance = d; tipAt = i; }
        }
        for (int i = 0; i < tipAt; i++)
            if ((points[i] - first).Length >= distance * TipRevisit) return i;
        return tipAt;
    }

    // ---------- Shared geometry ----------

    /// <summary>An arrow from <paramref name="from"/> to <paramref name="to"/>: shaft plus a filled head sized by the stroke width.</summary>
    public static Geometry Arrow(Point from, Point to, double width)
    {
        const double MaxHeadShare = 0.6, HeadPerWidth = 4, HeadBase = 10, ShaftShortening = 0.8, Spread = 0.42;
        var geometry = new PathGeometry();
        double length = (to - from).Length;
        if (length < 1) return geometry;
        double head = Math.Min(length * MaxHeadShare, HeadPerWidth * width + HeadBase), angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var shaft = new PathFigure { StartPoint = from, IsFilled = false };
        shaft.Segments.Add(new LineSegment(new Point(to.X - head * ShaftShortening * Math.Cos(angle), to.Y - head * ShaftShortening * Math.Sin(angle)), true));
        var point = new PathFigure { StartPoint = to, IsClosed = true, IsFilled = true };
        point.Segments.Add(new LineSegment(new Point(to.X - head * Math.Cos(angle + Spread), to.Y - head * Math.Sin(angle + Spread)), true));
        point.Segments.Add(new LineSegment(new Point(to.X - head * Math.Cos(angle - Spread), to.Y - head * Math.Sin(angle - Spread)), true));
        geometry.Figures.Add(shaft);
        geometry.Figures.Add(point);
        return geometry;
    }

    /// <summary>Smooths a freehand stroke: resample evenly, average neighbours, a soft curve through the midpoints.</summary>
    public static Geometry? Smooth(List<Point> raw)
    {
        const double Step = 3, MinLength = 8;
        double total = Length(raw);
        if (raw.Count < 3 || total < MinLength) return null;
        var points = Resample(raw, Step);
        if (points.Count < 4) return null;
        var smooth = WeightedAverage(points);
        var figure = new PathFigure { StartPoint = smooth[0], IsFilled = false };
        var curve = new PolyQuadraticBezierSegment();
        for (int i = 1; i < smooth.Count - 1; i++)
        {
            curve.Points.Add(smooth[i]);
            curve.Points.Add(new Point((smooth[i].X + smooth[i + 1].X) / 2, (smooth[i].Y + smooth[i + 1].Y) / 2));
        }
        figure.Segments.Add(curve);
        figure.Segments.Add(new LineSegment(smooth[smooth.Count - 1], true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // 1-2-3-2-1 weights inside, 1-2-1 next to the ends; the ends themselves stay.
    static List<Point> WeightedAverage(List<Point> points)
    {
        var smooth = new List<Point>(points);
        for (int i = 2; i < points.Count - 2; i++)
            smooth[i] = new Point((points[i - 2].X + points[i - 1].X * 2 + points[i].X * 3 + points[i + 1].X * 2 + points[i + 2].X) / 9,
                                  (points[i - 2].Y + points[i - 1].Y * 2 + points[i].Y * 3 + points[i + 1].Y * 2 + points[i + 2].Y) / 9);
        smooth[1] = new Point((points[0].X + points[1].X * 2 + points[2].X) / 4, (points[0].Y + points[1].Y * 2 + points[2].Y) / 4);
        int m = points.Count - 2;
        smooth[m] = new Point((points[m - 1].X + points[m].X * 2 + points[m + 1].X) / 4, (points[m - 1].Y + points[m].Y * 2 + points[m + 1].Y) / 4);
        return smooth;
    }

    /// <summary>True if <paramref name="degrees"/> is within <paramref name="tolerance"/> of a multiple of 90°.</summary>
    public static bool NearAxis(double degrees, double tolerance)
    {
        double rest = ((degrees % 90) + 90) % 90;
        return rest < tolerance || rest > 90 - tolerance;
    }

    /// <summary>Moves <paramref name="end"/> onto the horizontal or vertical through <paramref name="start"/>.</summary>
    public static Point SnapAxis(Point start, Point end) =>
        Math.Abs(end.X - start.X) >= Math.Abs(end.Y - start.Y) ? new Point(end.X, start.Y) : new Point(start.X, end.Y);

    static Point SnapNearAxis(Point start, Point end)
    {
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI;
        return NearAxis(angle, AxisSnapDegrees) ? SnapAxis(start, end) : end;
    }

    public static Rect Bounds(List<Point> points)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    static Point Center(Rect bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    // Splits a stroke into pieces of equal length.
    static List<Point> Resample(List<Point> points, double step)
    {
        var resampled = new List<Point> { points[0] };
        double carry = 0;
        for (int i = 1; i < points.Count; i++)
        {
            Point a = points[i - 1], b = points[i];
            double segment = (b - a).Length;
            if (segment < 1e-9) continue;
            double t = step - carry;
            while (t <= segment)
            {
                resampled.Add(a + (b - a) * (t / segment));
                t += step;
            }
            carry = segment - (t - step);
        }
        if ((resampled[resampled.Count - 1] - points[points.Count - 1]).Length > step * 0.3) resampled.Add(points[points.Count - 1]);
        return resampled;
    }

    static double Length(List<Point> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += (points[i] - points[i - 1]).Length;
        return length;
    }

    static double SegmentDistance(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9) return (p - a).Length;
        double t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / lengthSquared));
        return (p - (a + ab * t)).Length;
    }

    static double PolylineDistance(Point p, List<Point> polyline)
    {
        double distance = double.MaxValue;
        for (int i = 1; i < polyline.Count; i++) distance = Math.Min(distance, SegmentDistance(p, polyline[i - 1], polyline[i]));
        return distance;
    }

    // Mean distance in both directions: stroke to shape and shape to stroke.
    static double Mismatch(List<Point> stroke, List<Point> shape)
    {
        var shapePoints = Resample(shape, Math.Max(1, Length(shape) / Samples));
        double strokeToShape = 0, shapeToStroke = 0;
        foreach (var point in stroke) strokeToShape += PolylineDistance(point, shape);
        foreach (var point in shapePoints) shapeToStroke += PolylineDistance(point, stroke);
        return (strokeToShape / stroke.Count + shapeToStroke / shapePoints.Count) / 2;
    }

    static List<Point> Ring(params Point[] corners) => new(corners) { corners[0] };
}
