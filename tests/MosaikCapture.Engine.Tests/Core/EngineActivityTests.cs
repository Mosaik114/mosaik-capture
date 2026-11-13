namespace MosaikCapture.Engine.Tests.Core;

// The host's quit command and state query share one answer to "would quitting lose work?".
public sealed class EngineActivityTests
{
    static bool QuitNow()
    {
        bool shutDown = false;
        bool quit = CaptureBridge.Quit(() => shutDown = true);
        Assert.Equal(quit, shutDown);
        return quit;
    }

    static bool ReportedBusy => (CaptureBridge.State() & 1) != 0;

    [Fact]
    public void IdleEngineQuitsAndReportsIdle()
    {
        Assert.Equal(0, EngineActivity.Running);
        Assert.False(ReportedBusy);
        Assert.True(QuitNow());
    }

    [Fact]
    public void WorkCountsOnceHoweverOftenItBeginsOrEnds()
    {
        var first = new EngineWork();
        var second = new EngineWork();

        first.Begin();
        first.Begin();
        second.Begin();
        Assert.Equal(2, EngineActivity.Running);

        first.End();
        first.End();
        Assert.Equal(1, EngineActivity.Running);
        Assert.False(QuitNow());

        second.End();
        Assert.Equal(0, EngineActivity.Running);
    }

    [Fact]
    public void QuitIsRefusedWhileTheEditorIsOpen() => WpfThread.Run(() =>
    {
        var image = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 200 * 4], 320 * 4);
        image.Freeze();
        var editor = new ScreenshotEditor((_, _) => { }, _ => { }, (_, _) => { });
        try
        {
            editor.Open(new ScreenCapture
            {
                FullScreen = image,
                Image = image,
                Scale = 1,
                VirtualWidth = 320,
                VirtualHeight = 200,
                Crop = new Int32Rect(0, 0, 320, 200),
                Monitor = new RECT { left = 0, top = 0, right = 320, bottom = 200 },
            });

            Assert.True(ReportedBusy, "the host would think it may quit");
            Assert.False(QuitNow(), "quitting would discard the markup");
        }
        finally { editor.Abandon(); }

        Assert.False(ReportedBusy);
        Assert.True(QuitNow());
    });

    [Fact]
    public void QuitIsRefusedWhileAnExportRuns()
    {
        using var folder = new TempDirectory();
        string source = folder.File("clip.mp4");
        File.WriteAllText(source, "no video");   // ffmpeg fails on it, which ends the export
        var video = new RecordedVideo { Path = source, Duration = 2 };

        WpfThread.Run(() =>
        {
            bool done = false;
            VideoExport.Export(video, 0, 1, false, false, _ => { }, (_, _) => done = true);

            Assert.True(ReportedBusy, "the host would think it may quit");
            Assert.False(QuitNow(), "quitting would kill ffmpeg and leave a partial file");

            Assert.True(WpfThread.PumpUntil(() => done, 30000), "the export did not end");
            Assert.False(ReportedBusy);
            Assert.True(QuitNow());
        });
    }
}
