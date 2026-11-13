namespace MosaikCapture.Engine.Tests.Screenshot;

// Small pure rules of the overlay and the pinned screenshot.
public sealed class ScreenshotHelperTests
{
    [Theory]
    [InlineData(10, 20, 10, 20, false, 10, 20, 1, 1)]          // a click is one pixel
    [InlineData(10, 20, 109, 69, false, 10, 20, 100, 50)]      // both end pixels are included
    [InlineData(109, 69, 10, 20, false, 10, 20, 100, 50)]      // dragged up and left
    [InlineData(10, 20, 109, 69, true, 10, 20, 100, 100)]      // Shift: square of the longer side
    [InlineData(109, 69, 10, 20, true, 10, -30, 100, 100)]     // square grows towards the pointer
    public void DraggedAreaIncludesBothEndsAndCanBeSquare(double x0, double y0, double x1, double y1, bool square, double x, double y, double width, double height) =>
        Assert.Equal(new Rect(x, y, width, height), SelectionOverlay.DraggedArea(new Point(x0, y0), new Point(x1, y1), square));

    [Theory]
    [InlineData(0, 0, 0, "#000000")]
    [InlineData(255, 255, 255, "#FFFFFF")]
    [InlineData(0xD9, 0x77, 0x57, "#D97757")]
    public void HexColorIsUppercaseRgb(byte red, byte green, byte blue, string expected) => Assert.Equal(expected, SelectionOverlay.HexColor(red, green, blue));

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.04, 1.0)]     // snaps to 100 %
    [InlineData(0.96, 1.0)]
    [InlineData(1.2, 1.2)]
    [InlineData(0.01, 0.1)]     // at least 10 %
    [InlineData(9, 5)]          // at most 500 %
    public void ZoomIsClampedAndSnapsTo100Percent(double zoom, double expected) => Assert.Equal(expected, PinnedScreenshot.ClampZoom(zoom), 6);

    [Theory]
    [InlineData(1.0, -0.1, 0.9)]
    [InlineData(1.0, 0.1, 1.0)]     // never above 100 %
    [InlineData(0.3, -0.1, 0.2)]
    [InlineData(0.2, -0.1, 0.2)]    // never below 20 %
    [InlineData(0.54, 0.1, 0.6)]    // back onto the 10 % grid
    public void OpacityMovesIn10PercentSteps(double opacity, double step, double expected) => Assert.Equal(expected, PinnedScreenshot.StepOpacity(opacity, step), 6);

    [Fact]
    public void SaveAsWritesPngOrJpegByNameAndReportsFailures()
    {
        using var folder = new TempDirectory();
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);

        Assert.Equal(SaveAsResult.Saved, Screenshots.Write(image, folder.File("bild.png")));
        Assert.Equal(SaveAsResult.Saved, Screenshots.Write(image, folder.File("bild.JPG")));
        Assert.Equal(SaveAsResult.Failed, Screenshots.Write(image, Path.Combine(folder.Path, "fehlt", "bild.png")));

        Assert.Equal(new byte[] { 0x89, 0x50 }, File.ReadAllBytes(folder.File("bild.png")).Take(2));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(folder.File("bild.JPG")).Take(2));
    }

    [Fact]
    public void TheFolderIsReadOncePerCaptureNotOnEveryUse()
    {
        string previous = CaptureSettings.FileName;
        using var settings = new TestSettings("[capture]\r\nscreenshot_folder=C:\\Erste\r\n");
        try
        {
            CaptureSettings.UseFile(settings.FileName);
            new ScreenshotStore().NewPath(DateTime.Now);   // a capture reads the settings
            File.WriteAllText(settings.FileName, "[capture]\r\nscreenshot_folder=C:\\Zweite\r\n", Encoding.Unicode);

            Assert.Equal("C:\\Erste", Screenshots.Folder);   // the card, the editor and pins keep the capture's folder

            Assert.StartsWith("C:\\Zweite\\", new ScreenshotStore().NewPath(DateTime.Now));
            Assert.Equal("C:\\Zweite", Screenshots.Folder);
        }
        finally
        {
            CaptureSettings.UseFile(previous);
            Screenshots.ReadFolder();
        }
    }
}
