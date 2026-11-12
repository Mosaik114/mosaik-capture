namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>Helpers for tests that need a real ffmpeg (skipped when it is not installed).</summary>
internal static class FfmpegTool
{
    public static bool Available => Ffmpeg.Exe != null && Ffmpeg.Probe != null;

    public static void SkipIfMissing()
    {
        Assert.SkipUnless(Available, "ffmpeg/ffprobe not found (winget install ffmpeg)");
        Ffmpeg.DetectEncoderAsync().Wait();
    }

    public static void Run(params string[] args)
    {
        var result = Ffmpeg.RunAsync(Ffmpeg.Exe, FfmpegArgs.Quiet().Add(args), FfmpegTimeouts.ForMedia(60)).GetAwaiter().GetResult();   // test setup on a worker thread
        if (!result.Succeeded) throw new InvalidOperationException("ffmpeg " + result.ExitCode + ": " + result.Error);
    }

    /// <summary>A 1280 x 720 test pattern of the given length with a 440 Hz tone.</summary>
    public static void TestVideo(string path, int seconds) =>
        Run("-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=" + seconds, "-f", "lavfi", "-i", "sine=f=440:d=" + seconds,
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path);

    /// <summary>
    /// Runs ScreenRecorder.Export on the WPF thread and pumps the dispatcher until it reports back.
    /// </summary>
    public static (RecordedVideo? Result, string? Error) Export(RecordedVideo source, double from, double to, bool sound, bool gif, List<double> progress) =>
        WpfThread.Run(() =>
        {
            RecordedVideo? result = null;
            string? error = null;
            bool finished = false;
            VideoExport.Export(source, from, to, sound, gif, progress.Add, (r, e) => { result = r; error = e; finished = true; });
            Assert.True(WpfThread.PumpUntil(() => finished, 90000), "export did not finish");
            WpfThread.Pump(50);   // let the last progress callbacks arrive
            return (result, error);
        });
}

/// <summary>Creates the 6 s probe video once per test class.</summary>
public sealed class ProbeVideoFixture : IDisposable
{
    readonly TempDirectory directory = new();
    string? path;

    public string Path
    {
        get
        {
            FfmpegTool.SkipIfMissing();
            if (path == null)
            {
                string file = directory.File("probe.mp4");
                FfmpegTool.TestVideo(file, 6);
                path = file;
            }
            return path;
        }
    }

    /// <summary>A private copy of the probe video in its own directory, so exports never collide.</summary>
    internal string CopyTo(TempDirectory target, string name = "probe.mp4")
    {
        string copy = target.File(name);
        File.Copy(Path, copy);
        return copy;
    }

    public void Dispose() => directory.Dispose();
}
