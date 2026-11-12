namespace MosaikCapture.Engine.Tests.Screenshot;

// The editor's plain models: undo/redo, ink style and crop geometry (no window needed).
public sealed class EditorModelTests
{
    [Fact]
    public void UndoAndRedoReplayInOrder()
    {
        var history = new UndoHistory();
        var log = new List<string>();
        history.Push(() => log.Add("undo 1"), () => log.Add("redo 1"));
        history.Push(() => log.Add("undo 2"), () => log.Add("redo 2"));

        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.False(history.Undo());
        Assert.True(history.Redo());

        Assert.Equal(new[] { "undo 2", "undo 1", "redo 1" }, log);
        Assert.True(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void ANewChangeClearsRedo()
    {
        var history = new UndoHistory();
        history.Push(() => { }, () => { });
        history.Undo();

        history.Push(() => { }, () => { });

        Assert.False(history.CanRedo);
        Assert.False(history.Redo());
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var history = new UndoHistory();
        history.Push(() => { }, () => { });
        Assert.True(history.HasChanges);

        history.Clear();

        Assert.False(history.HasChanges);
        Assert.False(history.Undo());
    }

    [Fact]
    public void InkStyleStartsMediumAndSizesFollowTheStep()
    {
        var style = new InkStyle();

        Assert.Equal(2, style.WidthIndex);
        Assert.Equal(5, style.StrokeWidth);
        Assert.Equal(22, style.MarkerWidth);
        Assert.Equal(26, style.TextSize);
        Assert.Equal(30, style.BadgeSize);
        Assert.Equal(InkStyle.Colors[0], style.Ink);
        Assert.Equal(0x66, style.MarkerInk.A);
    }

    [Fact]
    public void WidthStepsStopAtBothEnds()
    {
        var style = new InkStyle();
        style.SetWidth(InkStyle.Steps - 1);

        Assert.False(style.StepWidth(1));
        Assert.True(style.StepWidth(-1));
        Assert.Equal(InkStyle.Steps - 2, style.WidthIndex);

        style.SetWidth(-7);
        Assert.Equal(0, style.WidthIndex);
        Assert.False(style.StepWidth(-1));
    }

    [Fact]
    public void ColorIsClampedToThePalette()
    {
        var style = new InkStyle();
        style.SetColor(99);

        Assert.Equal(InkStyle.Colors.Length - 1, style.ColorIndex);
    }

    static readonly Size Surface = new(1000, 800);

    [Fact]
    public void MovedCropStaysOnTheSurfaceAndOnWholePixels()
    {
        var crop = new Rect(100, 100, 200, 100);

        Assert.Equal(new Rect(150, 130, 200, 100), CropGeometry.Moved(crop, new Vector(50, 30), Surface, 1));
        Assert.Equal(new Rect(800, 0, 200, 100), CropGeometry.Moved(crop, new Vector(5000, -5000), Surface, 1));
        Assert.Equal(new Rect(100.5, 100, 200, 100), CropGeometry.Moved(crop, new Vector(0.4, 0.1), Surface, 2));   // 2x: half units
    }

    [Theory]
    [InlineData(0, 50, 60, 50, 60, 250, 140)]       // top left corner
    [InlineData(4, 400, 300, 100, 100, 300, 200)]   // bottom right corner
    [InlineData(1, 999, 20, 100, 20, 200, 180)]     // top edge: only the top moves
    [InlineData(0, 900, 900, 292, 192, 8, 8)]       // never below the minimum size
    public void ResizedCropFollowsItsHandle(int handle, double x, double y, double left, double top, double width, double height) =>
        Assert.Equal(new Rect(left, top, width, height), CropGeometry.Resized(new Rect(100, 100, 200, 100), handle, new Point(x, y), Surface, 1));

    [Fact]
    public void HandlesRunClockwiseFromTheTopLeft()
    {
        var crop = new Rect(10, 20, 100, 50);

        Assert.Equal(8, CropGeometry.HandleCount);
        Assert.Equal(new Point(10, 20), CropGeometry.HandleCenter(crop, 0));
        Assert.Equal(new Point(60, 20), CropGeometry.HandleCenter(crop, 1));
        Assert.Equal(new Point(110, 70), CropGeometry.HandleCenter(crop, 4));
        Assert.Equal(new Point(10, 45), CropGeometry.HandleCenter(crop, 7));
    }

    [Fact]
    public void CropInPixelsIsScaledAndStaysInsideTheImage()
    {
        Assert.Equal(new Int32Rect(20, 40, 200, 100), CropGeometry.InPixels(new Rect(10, 20, 100, 50), 2, 1000, 1000));
        Assert.Equal(new Int32Rect(950, 0, 50, 10), CropGeometry.InPixels(new Rect(950, 0, 100, 10), 1, 1000, 1000));
        Assert.Equal(new Int32Rect(5, 5, 1, 1), CropGeometry.InPixels(new Rect(5, 5, 0.1, 0.1), 1, 1000, 1000));
    }

    [Fact]
    public void ShiftDraggingSnapsAnglesAndSquares()
    {
        var area = new Rect(0, 0, 500, 500);

        Point snapped = CropGeometry.SnapTo45Degrees(new Point(100, 100), new Point(200, 110), area);
        Assert.InRange(snapped.X, 200.4, 200.6);   // same length, now horizontal
        Assert.Equal(100, snapped.Y, 6);
        Assert.Equal(new Point(160, 160), CropGeometry.SquareCorner(new Point(100, 100), new Point(130, 160), area));
        Assert.Equal(new Point(0, 0), CropGeometry.SquareCorner(new Point(100, 100), new Point(20, -400), area));   // kept inside
    }

    [Fact]
    public void ToolbarGoesBelowAboveOrInsideTheCrop()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var toolbar = new Size(600, 50);

        Assert.Equal(new Point(150, 314), CropGeometry.ToolbarPosition(new Rect(100, 100, 700, 200), toolbar, monitor));        // below
        Assert.Equal(new Point(150, 836), CropGeometry.ToolbarPosition(new Rect(100, 900, 700, 150), toolbar, monitor));        // above
        Assert.Equal(new Point(660, 1006), CropGeometry.ToolbarPosition(new Rect(0, 0, 1920, 1080), toolbar, monitor));        // inside
        Assert.Equal(12, CropGeometry.ToolbarPosition(new Rect(-500, 100, 100, 100), toolbar, monitor).X);                    // on the monitor
    }

    [Fact]
    public void SizeChipSitsAboveTheCropUnlessThereIsNoRoom()
    {
        var monitor = new Rect(0, 0, 1920, 1080);

        Assert.Equal(66, CropGeometry.SizeChipTop(new Rect(0, 100, 10, 10), monitor));
        Assert.Equal(20, CropGeometry.SizeChipTop(new Rect(0, 10, 10, 10), monitor));
    }
}
