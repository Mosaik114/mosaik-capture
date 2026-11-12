using System.Text.RegularExpressions;

namespace MosaikCapture.Engine.Tests.Recording;

// Recording pipeline with a real ffmpeg: assembling after pauses (audio stays in sync), trimming, GIF,
// thumbnails. Skipped when ffmpeg is not installed.
[Trait("Category", "Ffmpeg")]
public sealed class FfmpegIntegrationTests : IClassFixture<ProbeVideoFixture>, IDisposable
{
    readonly ProbeVideoFixture probe;
    readonly TempDirectory work = new();

    public FfmpegIntegrationTests(ProbeVideoFixture probe) => this.probe = probe;

    public void Dispose() => work.Dispose();

    // ---------- Assembling after pauses ----------

    /// <summary>Silent 48 kHz track (like the recorder writes) with a short beep at <paramref name="beepAt"/> seconds.</summary>
    string ToneWithBeep(string name, double length, double beepAt)
    {
        string path = work.File(name);
        Ffmpeg.Run("-f lavfi -i \"sine=f=1000:r=48000:d=" + RecUi.Num(length) + "\" -af \"volume='between(t," + RecUi.Num(beepAt) + "," +
            RecUi.Num(beepAt + 0.2) + ")':eval=frame\" -c:a pcm_s16le \"" + path + "\"");
        return path;
    }

    /// <summary>
    /// Start of every beep: the end of a silence that is followed by another silence
    /// (ffmpeg reports one more silence end at the end of the file, which is no beep).
    /// </summary>
    static List<double> Beeps(string video)
    {
        FF.Run(FF.Exe, "-hide_banner -nostats -i \"" + video + "\" -map 0:a -af silencedetect=noise=-30dB:d=0.1 -f null -", null, out _, out string error);
        var beeps = new List<double>();
        double end = -1;
        foreach (Match m in Regex.Matches(error, @"silence_(start|end): ([\d.]+)"))
        {
            double t = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "end") end = t;
            else if (end >= 0) { beeps.Add(end); end = -1; }
        }
        return beeps;
    }

    // Two video segments (2 s and 3 s) with a pause in between. On the audio clock segment 1 starts at
    // 1.0 s and segment 2 at 5.5 s, so a beep at 6.0 s must end up at 2 + 0.5 = 2.5 s in the result.
    List<string> TwoSegments(string tag)
    {
        string dir = Directory.CreateDirectory(work.File("segments-" + tag)).FullName;
        var segments = new List<string> { Path.Combine(dir, "teil0.mkv"), Path.Combine(dir, "teil1.mkv") };
        Ffmpeg.Run("-f lavfi -i testsrc2=s=320x240:r=30:d=2 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"" + segments[0] + "\"");
        Ffmpeg.Run("-f lavfi -i testsrc2=s=320x240:r=30:d=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"" + segments[1] + "\"");
        return segments;
    }

    static void Assemble(List<string> segments, List<string> tracks, string target)
    {
        string args = RecSession.Assemble(Path.GetDirectoryName(segments[0])!, segments, tracks, new List<double> { 1.0, 5.5 }, new List<double> { 2.0, 3.0 }, target);
        Assert.True(FF.Run(FF.Exe, args, null, out _, out string error) == 0, "ffmpeg: " + error);
    }

    [Fact]
    public void SystemSoundAndMicrophoneStayInSyncAfterAPause()
    {
        Ffmpeg.SkipIfMissing();
        var segments = TwoSegments("two-tracks");
        string system = ToneWithBeep("ton.wav", 10, 6.0);    // ends up at 2.5 s
        string mic = ToneWithBeep("mikro.wav", 10, 1.5);     // ends up at 0.5 s
        string target = work.File("zusammen.mp4");

        Assemble(segments, new List<string> { system, mic }, target);

        var result = WpfThread.Run(() => RecResult.Load(target));
        Assert.InRange(result.Duration, 4.9, 5.1);
        Assert.True(result.Audio, "audio missing");
        var beeps = Beeps(target);
        Assert.True(beeps.Count == 2, "beeps: " + string.Join(", ", beeps));
        Assert.InRange(beeps[0], 0.45, 0.55);   // microphone
        Assert.InRange(beeps[1], 2.45, 2.55);   // system sound after the pause
    }

    [Fact]
    public void OneTrackOrNoAudioAtAll()
    {
        Ffmpeg.SkipIfMissing();
        var segments = TwoSegments("one-track");
        string target = work.File("eine-spur.mp4");
        Assemble(segments, new List<string> { ToneWithBeep("nur-ton.wav", 10, 6.0) }, target);
        var beeps = Beeps(target);
        Assert.Single(beeps);
        Assert.InRange(beeps[0], 2.45, 2.55);

        segments = TwoSegments("no-audio");
        target = work.File("ohne-ton.mp4");
        Assemble(segments, new List<string>(), target);
        var result = WpfThread.Run(() => RecResult.Load(target));
        Assert.InRange(result.Duration, 4.9, 5.1);
        Assert.False(result.Audio, "should have no audio");
    }

    // ---------- Loading, trimming, GIF ----------

    [Fact]
    public void LoadReadsDurationSizeAudioAndThumbnail()
    {
        string video = probe.Path;

        var result = WpfThread.Run(() => RecResult.Load(video));

        Assert.InRange(result.Duration, 5.95, 6.05);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.True(result.Audio);
        Assert.False(result.Gif);
        Assert.Equal(new FileInfo(video).Length, result.Bytes);
        Assert.NotNull(result.Thumb);
        Assert.Equal(960, result.Thumb!.PixelWidth);
        Assert.Equal(540, result.Thumb.PixelHeight);
    }

    [Fact]
    public void TrimKeepsTheSelectionAndTheSoundWithMonotonicProgress()
    {
        var source = WpfThread.Run(() => RecResult.Load(probe.CopyTo(work)));
        var progress = new List<double>();

        var (result, error) = Ffmpeg.Export(source, 1.0, 4.5, sound: true, gif: false, progress);

        Assert.True(result != null, "export failed: " + error);
        Assert.Equal(work.File("probe gekürzt.mp4"), result!.Path);
        Assert.InRange(result.Duration, 3.4, 3.6);
        Assert.True(result.Audio, "audio missing");
        Assert.Equal(1280, result.Width);
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.InRange(p, 0, 1));
        for (int i = 1; i < progress.Count; i++) Assert.True(progress[i] >= progress[i - 1], "progress went backwards");
        Assert.True(progress[progress.Count - 1] >= 0.9, "last progress only " + progress[progress.Count - 1]);
    }

    [Fact]
    public void TrimWithoutSoundAndASecondExportDoesNotOverwriteTheFirst()
    {
        var source = WpfThread.Run(() => RecResult.Load(probe.CopyTo(work)));

        var (first, _) = Ffmpeg.Export(source, 0, 2, sound: false, gif: false, new List<double>());
        var (second, error) = Ffmpeg.Export(source, 0, 2, sound: false, gif: false, new List<double>());

        Assert.True(second != null, "export failed: " + error);
        Assert.Equal(work.File("probe gekürzt.mp4"), first!.Path);
        Assert.Equal(work.File("probe gekürzt (2).mp4"), second!.Path);
        Assert.False(second.Audio, "should have no audio");
        Assert.InRange(second.Duration, 1.9, 2.1);
    }

    [Fact]
    public void GifIsAtMost960PixelsWideAndKeepsTheAspectRatio()
    {
        var source = WpfThread.Run(() => RecResult.Load(probe.CopyTo(work)));
        var progress = new List<double>();

        var (result, error) = Ffmpeg.Export(source, 2, 5, sound: false, gif: true, progress);

        Assert.True(result != null, "export failed: " + error);
        Assert.True(result!.Gif);
        Assert.Equal(work.File("probe.gif"), result.Path);
        Assert.Equal(960, result.Width);
        Assert.Equal(540, result.Height);
        Assert.InRange(result.Duration, 2.85, 3.15);
        Assert.True(progress[progress.Count - 1] >= 0.9, "last progress only " + progress[progress.Count - 1]);
    }

    [Fact]
    public void BrokenFileReportsAnErrorAndLeavesNoPartialOutput()
    {
        Ffmpeg.SkipIfMissing();
        string broken = work.File("kaputt.mp4");
        File.WriteAllBytes(broken, new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 1, 2, 3, 4, 5, 6, 7, 8 });

        var (result, error) = Ffmpeg.Export(new RecResult { Path = broken, Duration = 5, Audio = true }, 1, 3, sound: true, gif: false, new List<double>());

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.NotEqual("Unbekannter Fehler", error);
        Assert.False(File.Exists(work.File("kaputt gekürzt.mp4")), "partial output file is left behind");
    }

    // ---------- Thumbnails ----------

    [Fact]
    public void FilmStripAndSingleFrameHaveTheRequestedSize()
    {
        string video = probe.Path;

        var (strip, frame, missing) = WpfThread.Run(() => (RecResult.Strip(video, 6, 8, 60), RecResult.Frame(video, 3, 640), RecResult.Frame(work.File("gibt-es-nicht.mp4"), 1, 640)));

        Assert.NotNull(strip);
        Assert.Equal(8 * 106, strip!.PixelWidth);   // 8 frames of 106 px (16:9 at 60 px height)
        Assert.Equal(60, strip.PixelHeight);
        Assert.NotNull(frame);
        Assert.Equal(640, frame!.PixelWidth);
        Assert.Equal(360, frame.PixelHeight);
        Assert.Null(missing);
    }
}
