namespace MosaikCapture.Engine.Tests.Recording;

// Logic of the windows shown while recording. The windows only get a (hidden) handle; nothing is shown.
public sealed class LiveWindowTests
{
    static readonly RECT Monitor = new() { left = 0, top = 0, right = 1920, bottom = 1080 };

    [Fact]
    public void FrameWindowIsTheAreaPlusSixPixels() => WpfThread.Run(() =>
    {
        var frame = new RecordingFrame();

        Assert.Equal(new Int32Rect(34, 34, 612, 352), frame.Build(new Int32Rect(40, 40, 600, 340), Monitor, 1));
        var canvas = Assert.IsType<System.Windows.Controls.Canvas>(frame.Content);
        Assert.Equal(12, canvas.Children.Count);   // four edges and eight corner arms
        Assert.Equal(612, canvas.Width);
        Assert.Equal(352, canvas.Height);
    });

    [Fact]
    public void FrameNeverReachesOntoAnotherMonitor() => WpfThread.Run(() =>
    {
        var frame = new RecordingFrame();

        Assert.Equal(new Int32Rect(0, 0, 606, 346), frame.Build(new Int32Rect(0, 0, 600, 340), Monitor, 1));
        Assert.Equal(new Int32Rect(0, 0, 1920, 1080), frame.Build(new Int32Rect(0, 0, 1920, 1080), Monitor, 1));
        Assert.Equal(new Int32Rect(1914, 1074, 6, 6), frame.Build(new Int32Rect(1920, 1080, 0, 0), Monitor, 1));
    });

    [Fact]
    public void FrameUsesWpfUnitsOnScaledMonitors() => WpfThread.Run(() =>
    {
        var frame = new RecordingFrame();

        Assert.Equal(new Int32Rect(34, 34, 612, 352), frame.Build(new Int32Rect(40, 40, 600, 340), Monitor, 2));
        var canvas = (System.Windows.Controls.Canvas)frame.Content;
        Assert.Equal(306, canvas.Width);
        Assert.Equal(176, canvas.Height);
    });

    [Fact]
    public void PausedFrameUsesTheMutedColor() => WpfThread.Run(() =>
    {
        var frame = new RecordingFrame();
        frame.Build(new Int32Rect(40, 40, 600, 340), Monitor, 1);
        var canvas = (System.Windows.Controls.Canvas)frame.Content;
        var muted = (Brush)frame.Resources["Muted"];
        var accent = (Brush)frame.Resources["Accent"];

        Assert.All(canvas.Children.Cast<System.Windows.Shapes.Rectangle>(), r => Assert.Same(accent, r.Fill));
        frame.SetPaused(true);
        Assert.All(canvas.Children.Cast<System.Windows.Shapes.Rectangle>(), r => Assert.Same(muted, r.Fill));
        frame.SetPaused(false);
        Assert.All(canvas.Children.Cast<System.Windows.Shapes.Rectangle>(), r => Assert.Same(accent, r.Fill));
    });

    [Fact]
    public void CountdownOfZeroStartsImmediatelyWithoutShowingAWindow()
    {
        using var settings = new TestSettings("[capture]\r\ncountdown=0\r\n");
        WpfThread.Run(() =>
        {
            var calls = new List<string>();
            var countdown = new CountdownWindow();

            countdown.Run(new Int32Rect(100, 100, 640, 360), Monitor, () => calls.Add("launch"), () => calls.Add("done"), () => calls.Add("abort"));

            Assert.Equal(new[] { "launch", "done" }, calls);
            Assert.False(countdown.IsVisible);
        });
    }

    [Fact]
    public void LiveWindowsAreToolWindowsThatNeverTakeFocus() => WpfThread.Run(() =>
    {
        var windows = new Window[] { new RecordingFrame(), new CountdownWindow(), new RecordingToolbar(), new RecordingCard() };

        foreach (var window in windows)
        {
            Assert.True(window.Topmost, window.Title);
            Assert.False(window.ShowInTaskbar, window.Title);
            Assert.False(window.ShowActivated, window.Title);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.False(window.IsVisible, window.Title);
        }
        Assert.Equal(new[] { "Mosaiks-Aufnahme Rahmen", "Mosaiks-Aufnahme Countdown", "Mosaiks-Aufnahme Leiste", "Mosaiks-Aufnahme Karte" }, windows.Select(w => w.Title));
    });
}
