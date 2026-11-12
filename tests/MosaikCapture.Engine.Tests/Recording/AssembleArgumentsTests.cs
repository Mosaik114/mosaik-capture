namespace MosaikCapture.Engine.Tests.Recording;

// The ffmpeg command that joins the recorded segments and lays the audio tracks underneath (no ffmpeg needed).
public sealed class AssembleArgumentsTests : IDisposable
{
    readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    string List => Path.Combine(directory.Path, "segments.txt");

    IReadOnlyList<string> Assemble(IList<string> tracks, string target = "C:\\Videos\\out.mp4") => RecordingSession.Assemble(
        List,
        tracks,
        new List<double> { 1.0, 5.5 },
        new List<double> { 2.0, 3.0 / 7 },
        target).Arguments;

    static string FilterOf(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void ConcatListUsesForwardSlashesAndEscapedQuotes() =>
        Assert.Equal(new[] { "file 'C:/Temp/segment-01.mkv'", "file 'C:/Temp/it'\\''s/segment-02.mkv'" },
            RecordingSession.ConcatList(new[] { "C:\\Temp\\segment-01.mkv", "C:\\Temp\\it's\\segment-02.mkv" }));

    [Fact]
    public void AssemblingWritesNothing()
    {
        Assemble(new List<string> { "C:\\Temp\\system.wav" });

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void WithoutAudioOnlyTheVideoIsCopied()
    {
        var args = Assemble(new List<string>());

        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", List,
            "-map", "0:v", "-c:v", "copy", "-movflags", "+faststart", "C:\\Videos\\out.mp4",
        }, args);
    }

    [Fact]
    public void OneTrackIsTrimmedPerSegmentAndPassedThrough()
    {
        var args = Assemble(new List<string> { "C:\\Temp\\system.wav" });

        Assert.Equal(new[] { "-i", List, "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "C:\\Temp\\system.wav", "-filter_complex" }, args.Skip(8).Take(11));
        Assert.Equal(
            "[1:a]asplit=2[s0_0][s0_1];" +
            "[s0_0]atrim=start=1:duration=2,asetpts=PTS-STARTPTS,apad=whole_dur=2[t0_0];" +
            "[s0_1]atrim=start=5.5:duration=0.428571,asetpts=PTS-STARTPTS,apad=whole_dur=0.428571[t0_1];" +
            "[t0_0][t0_1]concat=n=2:v=0:a=1[c0];[c0]anull[a]", FilterOf(args));
        Assert.Equal(new[] { "-map", "0:v", "-map", "[a]", "-c:a", "aac", "-b:a", "192k", "-c:v", "copy", "-movflags", "+faststart", "C:\\Videos\\out.mp4" },
            args.Skip(20));
    }

    [Fact]
    public void TwoTracksAreMixedWithoutNormalization()
    {
        var args = Assemble(new List<string> { "C:\\Temp\\system.wav", "C:\\Temp\\microphone.wav" });

        Assert.Equal(new[] { "-i", "C:\\Temp\\system.wav", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "C:\\Temp\\microphone.wav" }, args.Skip(16).Take(10));
        string filter = FilterOf(args);
        Assert.Contains("[2:a]asplit=2[s1_0][s1_1];", filter);
        Assert.Contains("concat=n=2:v=0:a=1[c1];", filter);
        Assert.EndsWith("[c0][c1]amix=inputs=2:normalize=0:duration=longest[a]", filter);
    }

    [Fact]
    public void TargetWithSpacesIsQuotedOnTheCommandLine()
    {
        string line = RecordingSession.Assemble(List, new List<string>(), new List<double> { 0 }, new List<double> { 1 },
            "C:\\Users\\Jane Doe\\Videos\\Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4").ToString();

        Assert.EndsWith(" \"C:\\Users\\Jane Doe\\Videos\\Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4\"", line);
    }

    [Fact]
    public void NumbersStayInvariantOnGermanWindows()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string filter = FilterOf(Assemble(new List<string> { "C:\\Temp\\system.wav" }));

            Assert.Contains("start=5.5:", filter);
            Assert.DoesNotContain("5,5", filter);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
