namespace MosaikCapture.Engine.Tests.Screenshot;

// Pen held still at the end of a stroke: ScreenshotEditor.Recognize turns hand-drawn strokes into clean shapes.
// The strokes jitter like a real hand (slow wave plus noise); each shape is checked with several seeds.
public sealed class ShapeRecognitionTests
{
    const int Seeds = 5;

    static ShapeKind Recognize(List<Point> stroke) => WpfThread.Run(() =>
    {
        ShapeRecognizer.Recognize(stroke, 3, out ShapeKind kind);
        return kind;
    });

    static void RecognizedAs(ShapeKind expected, Func<int, List<Point>> draw, string what)
    {
        for (int seed = 1; seed <= Seeds; seed++)
        {
            ShapeKind actual = Recognize(draw(seed));
            Assert.True(actual == expected, what + " (seed " + seed + "): expected " + expected + ", recognized " + actual);
        }
    }

    [Fact]
    public void CirclesAndEllipsesEvenIfNotQuiteClosedOrDrawnPastTheStart()
    {
        RecognizedAs(ShapeKind.Ellipse, s => Strokes.Wobble(Strokes.Ellipse(new Point(80, 80), 60, 60, 0.08), 3.5, s), "circle, a bit too far");
        RecognizedAs(ShapeKind.Ellipse, s => Strokes.Wobble(Strokes.Ellipse(new Point(80, 80), 70, 40, -0.06), 3, s + 10), "flat ellipse, not quite closed");
    }

    [Fact]
    public void NearlyRoundEllipseBecomesAPerfectCircle() => WpfThread.Run(() =>
    {
        var geometry = ShapeRecognizer.Recognize(Strokes.Wobble(Strokes.Ellipse(new Point(200, 200), 62, 56, 0.05), 1.5, 4), 3, out ShapeKind kind);

        Assert.Equal(ShapeKind.Ellipse, kind);
        var ellipse = Assert.IsType<EllipseGeometry>(geometry);
        Assert.Equal(ellipse.RadiusX, ellipse.RadiusY, 6);
    });

    [Fact]
    public void RectanglesEvenIfDrawnSlanted()
    {
        RecognizedAs(ShapeKind.Rectangle, s => Strokes.Wobble(Strokes.Polygon(0.15, new Point(10, 20), new Point(170, 24), new Point(168, 120), new Point(12, 116)), 3, s), "slanted rectangle");
        RecognizedAs(ShapeKind.Rectangle, s => Strokes.Wobble(Strokes.Polygon(0, new Point(20, 20), new Point(120, 20), new Point(120, 120), new Point(20, 120)), 2.5, s + 20), "square");
    }

    [Fact]
    public void RectangleLiesWhereItWasDrawn() => WpfThread.Run(() =>
    {
        var geometry = ShapeRecognizer.Recognize(Strokes.Wobble(Strokes.Polygon(0.1, new Point(200, 300), new Point(420, 300), new Point(420, 420), new Point(200, 420)), 2, 3), 3, out ShapeKind kind);

        Assert.Equal(ShapeKind.Rectangle, kind);
        Rect bounds = geometry!.Bounds;
        Assert.InRange(bounds.Left, 196, 204);
        Assert.InRange(bounds.Right, 416, 424);
        Assert.InRange(bounds.Top, 296, 304);
        Assert.InRange(bounds.Bottom, 416, 424);
    });

    [Fact]
    public void TrianglesIsoscelesAndFree()
    {
        RecognizedAs(ShapeKind.Triangle, s => Strokes.Wobble(Strokes.Polygon(0.1, new Point(80, 10), new Point(150, 130), new Point(10, 128)), 3, s), "tip at the top");
        RecognizedAs(ShapeKind.Triangle, s => Strokes.Wobble(Strokes.Polygon(0.05, new Point(10, 20), new Point(150, 40), new Point(60, 140)), 3, s + 30), "free triangle");
    }

    [Fact]
    public void Rhombus() =>
        RecognizedAs(ShapeKind.Rhombus, s => Strokes.Wobble(Strokes.Polygon(0.1, new Point(80, 10), new Point(150, 80), new Point(80, 150), new Point(10, 80)), 3, s), "rhombus");

    [Fact]
    public void LinesNearlyHorizontalSnapToTheAxisSlantedStaySlanted()
    {
        RecognizedAs(ShapeKind.Line, s => Strokes.Wobble(Strokes.Line(new Point(10, 60), new Point(170, 66), 60), 2, s), "nearly horizontal");
        RecognizedAs(ShapeKind.Line, s => Strokes.Wobble(Strokes.Line(new Point(20, 20), new Point(140, 140), 60), 2, s + 40), "diagonal");

        WpfThread.Run(() =>
        {
            var nearlyFlat = (LineGeometry)ShapeRecognizer.Recognize(Strokes.Wobble(Strokes.Line(new Point(10, 60), new Point(170, 66), 60), 2, 1), 3, out _)!;
            Assert.Equal(nearlyFlat.StartPoint.Y, nearlyFlat.EndPoint.Y);

            var slanted = (LineGeometry)ShapeRecognizer.Recognize(Strokes.Line(new Point(0, 0), new Point(173, 100), 60), 3, out _)!;
            double angle = Math.Atan2(slanted.EndPoint.Y - slanted.StartPoint.Y, slanted.EndPoint.X - slanted.StartPoint.X) * 180 / Math.PI;
            Assert.InRange(angle, 29.5, 30.5);
        });
    }

    [Fact]
    public void NearlyVerticalLineSnapsToTheAxis() => WpfThread.Run(() =>
    {
        var line = Assert.IsType<LineGeometry>(ShapeRecognizer.Recognize(Strokes.Line(new Point(50, 10), new Point(55, 170), 60), 3, out _));

        Assert.Equal(line.StartPoint.X, line.EndPoint.X);
    });

    [Fact]
    public void ArrowsWithOneOrTwoHintedBarbs()
    {
        RecognizedAs(ShapeKind.Arrow, s =>
        {
            var p = Strokes.Line(new Point(10, 130), new Point(150, 30), 50);
            p.AddRange(Strokes.Line(new Point(150, 30), new Point(118, 36), 10));
            return Strokes.Wobble(p, 1.5, s);
        }, "one barb");
        RecognizedAs(ShapeKind.Arrow, s =>
        {
            var p = Strokes.Line(new Point(10, 80), new Point(160, 80), 50);
            p.AddRange(Strokes.Line(new Point(160, 80), new Point(135, 60), 8));
            p.AddRange(Strokes.Line(new Point(135, 60), new Point(160, 80), 8));
            p.AddRange(Strokes.Line(new Point(160, 80), new Point(135, 100), 8));
            return Strokes.Wobble(p, 1.5, s + 50);
        }, "two barbs");
    }

    [Fact]
    public void ArrowPointsToTheTip() => WpfThread.Run(() =>
    {
        var p = Strokes.Line(new Point(10, 80), new Point(160, 80), 50);
        p.AddRange(Strokes.Line(new Point(160, 80), new Point(135, 60), 8));

        var arrow = Assert.IsType<PathGeometry>(ShapeRecognizer.Recognize(p, 3, out ShapeKind kind));
        Assert.Equal(ShapeKind.Arrow, kind);
        Assert.Equal(2, arrow.Figures.Count);   // shaft and filled head
        Assert.Equal(new Point(10, 80), arrow.Figures[0].StartPoint);
        Point tip = arrow.Figures[1].StartPoint;   // first arrival near the tip, snapped onto the shaft axis
        Assert.Equal(80, tip.Y);
        Assert.InRange(tip.X, 150, 160);
        Assert.True(arrow.Figures[1].IsFilled);
    });

    [Fact]
    public void ScribblesHalfArcsWavesAndShortStrokesStayFreehand()
    {
        RecognizedAs(ShapeKind.None, s =>
        {
            var p = new List<Point>();
            for (int i = 0; i < 90; i++) p.Add(new Point(90 + 60 * Math.Sin(i * 0.37 + s) * Math.Cos(i * 0.05), 80 + 50 * Math.Sin(i * 0.61 + 1)));
            return p;
        }, "scribble");
        RecognizedAs(ShapeKind.None, s => Strokes.Wobble(Strokes.Ellipse(new Point(80, 80), 60, 60, -0.45), 2, s), "half arc");
        RecognizedAs(ShapeKind.None, s =>
        {
            var p = new List<Point>();
            for (int x = 10; x <= 170; x += 2) p.Add(new Point(x, 80 + 30 * Math.Sin(x / 15.0)));
            return Strokes.Wobble(p, 1, s);
        }, "wave");
        RecognizedAs(ShapeKind.None, s => Strokes.Line(new Point(10, 10), new Point(20, 14), 5), "short stroke");
    }

    [Fact]
    public void DegenerateInputIsNotAShape()
    {
        Assert.Equal(ShapeKind.None, Recognize(new List<Point>()));
        Assert.Equal(ShapeKind.None, Recognize(new List<Point> { new(5, 5) }));
        Assert.Equal(ShapeKind.None, Recognize(Enumerable.Repeat(new Point(5, 5), 50).ToList()));
    }
}
