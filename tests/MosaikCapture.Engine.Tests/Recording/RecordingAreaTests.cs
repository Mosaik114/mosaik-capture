namespace MosaikCapture.Engine.Tests.Recording;

// ScreenRecorder.Fit: the recorded area must lie on one monitor, have even dimensions (yuv420p) and a minimum size (NVENC).
public sealed class RecordingAreaTests
{
    static RECT Monitor(int left, int top, int right, int bottom) => new() { left = left, top = top, right = right, bottom = bottom };

    static readonly RECT FullHd = Monitor(0, 0, 1920, 1080);

    [Fact]
    public void OddDimensionsBecomeEven() =>
        Assert.Equal(new Int32Rect(101, 51, 300, 200), RecordingArea.Fit(new Int32Rect(101, 51, 301, 201), FullHd));

    [Fact]
    public void AreaInsideTheMonitorIsKept() =>
        Assert.Equal(new Int32Rect(10, 20, 640, 360), RecordingArea.Fit(new Int32Rect(10, 20, 640, 360), FullHd));

    [Fact]
    public void WholeMonitorIsKept() =>
        Assert.Equal(new Int32Rect(0, 0, 1920, 1080), RecordingArea.Fit(new Int32Rect(0, 0, 1920, 1080), FullHd));

    [Theory]
    [InlineData(1700, 900, 400, 300, 1700, 900, 220, 180)]   // bottom right
    [InlineData(-50, -50, 300, 200, 0, 0, 250, 150)]         // top left
    public void OverhangIsCutOff(int x, int y, int w, int h, int ex, int ey, int ew, int eh) =>
        Assert.Equal(new Int32Rect(ex, ey, ew, eh), RecordingArea.Fit(new Int32Rect(x, y, w, h), FullHd));

    [Theory]
    [InlineData(490, 290, 20, 20, 420, 252)]    // grows around its center
    [InlineData(5, 5, 10, 10, 0, 0)]            // top-left corner
    [InlineData(1905, 1070, 10, 10, 1760, 984)] // bottom-right corner
    public void TinyAreaGrowsToTheMinimumSize(int x, int y, int w, int h, int ex, int ey) =>
        Assert.Equal(new Int32Rect(ex, ey, 160, 96), RecordingArea.Fit(new Int32Rect(x, y, w, h), FullHd));

    [Fact]
    public void SecondaryMonitorLeftOfThePrimaryHasNegativeCoordinates()
    {
        var left = Monitor(-1920, 0, 0, 1080);

        Assert.Equal(new Int32Rect(-1500, 100, 800, 600), RecordingArea.Fit(new Int32Rect(-1500, 100, 800, 600), left));
        Assert.Equal(new Int32Rect(-160, 500, 160, 200), RecordingArea.Fit(new Int32Rect(-100, 500, 300, 200), left));
    }

    [Fact]
    public void AnySelectionYieldsAValidArea()
    {
        var random = new Random(42);
        var monitors = new[] { FullHd, Monitor(-2560, -300, 0, 1140), Monitor(1920, 0, 3200, 1024) };
        for (int i = 0; i < 5000; i++)
        {
            var m = monitors[i % monitors.Length];
            var area = new Int32Rect(random.Next(m.left - 300, m.right + 300), random.Next(m.top - 300, m.bottom + 300), random.Next(0, 2500), random.Next(0, 1500));
            var fitted = RecordingArea.Fit(area, m);

            string what = "selection " + area + " on monitor " + m.left + "," + m.top + "," + m.right + "," + m.bottom + " gave " + fitted;
            Assert.True(fitted.X >= m.left && fitted.Y >= m.top && fitted.X + fitted.Width <= m.right && fitted.Y + fitted.Height <= m.bottom, what + " (outside)");
            Assert.True(fitted.Width % 2 == 0 && fitted.Height % 2 == 0, what + " (odd)");
            Assert.True(fitted.Width >= 160 && fitted.Height >= 96, what + " (too small)");
        }
    }
}
