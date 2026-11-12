namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>Synthetic freehand strokes for the shape recognition and editor tests.</summary>
internal static class Strokes
{
    public static List<Point> Line(Point a, Point b, int segments)
    {
        var points = new List<Point>();
        for (int i = 0; i <= segments; i++) points.Add(a + (b - a) * ((double)i / segments));
        return points;
    }

    /// <summary>
    /// One lap starting at 12 o'clock. <paramref name="overshoot"/> &gt; 0 draws past the start,
    /// &lt; 0 stops short of it.
    /// </summary>
    public static List<Point> Ellipse(Point center, double rx, double ry, double overshoot)
    {
        var points = new List<Point>();
        const int n = 80;
        for (int i = 0; i <= n * (1 + overshoot); i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / n;
            points.Add(new Point(center.X + rx * Math.Cos(a), center.Y + ry * Math.Sin(a)));
        }
        return points;
    }

    /// <summary>Traces the polygon once and then <paramref name="overshoot"/> of its first edge again.</summary>
    public static List<Point> Polygon(double overshoot, params Point[] corners)
    {
        var points = new List<Point>();
        const int perEdge = 25;
        for (int k = 0; k < corners.Length; k++)
        {
            Point a = corners[k], b = corners[(k + 1) % corners.Length];
            for (int i = 0; i < perEdge; i++) points.Add(a + (b - a) * ((double)i / perEdge));
        }
        for (int i = 0; i <= perEdge * overshoot; i++) points.Add(corners[0] + (corners[1] - corners[0]) * ((double)i / perEdge));
        return points;
    }

    /// <summary>Hand-like jitter: a slow wave plus a little noise, reproducible per seed.</summary>
    public static List<Point> Wobble(List<Point> points, double amplitude, int seed)
    {
        var random = new Random(seed);
        double phaseX = random.NextDouble() * 6, phaseY = random.NextDouble() * 6;
        return points.Select((p, i) => new Point(
            p.X + amplitude * Math.Sin(i * 0.13 + phaseX) + (random.NextDouble() - 0.5) * amplitude * 0.6,
            p.Y + amplitude * Math.Cos(i * 0.11 + phaseY) + (random.NextDouble() - 0.5) * amplitude * 0.6)).ToList();
    }
}
