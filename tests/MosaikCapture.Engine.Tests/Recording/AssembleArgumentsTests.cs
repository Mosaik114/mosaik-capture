namespace MosaikCapture.Engine.Tests.Recording;

// The ffmpeg command that joins the recorded segments and lays the audio tracks underneath (no ffmpeg needed).
public sealed class AssembleArgumentsTests : IDisposable
{
    readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    string Assemble(IList<string> tracks, string target = "C:\\Videos\\out.mp4") => RecSession.Assemble(
        directory.Path,
        new[] { "C:\\Temp\\teil0.mkv", "C:\\Temp\\it's\\teil1.mkv" },
        tracks,
        new List<double> { 1.0, 5.5 },
        new List<double> { 2.0, 3.0 / 7 },
        target);

    [Fact]
    public void WritesAConcatListWithForwardSlashesAndEscapedQuotes()
    {
        Assemble(new List<string>());

        var lines = File.ReadAllLines(Path.Combine(directory.Path, "teile.txt"));
        Assert.Equal(new[] { "file 'C:/Temp/teil0.mkv'", "file 'C:/Temp/it'\\''s/teil1.mkv'" }, lines);
    }

    [Fact]
    public void WithoutAudioOnlyTheVideoIsCopied()
    {
        string args = Assemble(new List<string>());

        string list = Path.Combine(directory.Path, "teile.txt");
        Assert.Equal("-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\" -map 0:v -c:v copy -movflags +faststart \"C:\\Videos\\out.mp4\"", args);
    }

    [Fact]
    public void OneTrackIsTrimmedPerSegmentAndPassedThrough()
    {
        string args = Assemble(new List<string> { "C:\\Temp\\ton.wav" });

        Assert.Contains(" -i \"C:\\Temp\\ton.wav\" -filter_complex \"", args);
        Assert.Contains("[1:a]asplit=2[s0_0][s0_1];", args);
        Assert.Contains("[s0_0]atrim=start=1:duration=2,asetpts=PTS-STARTPTS,apad=whole_dur=2[t0_0];", args);
        Assert.Contains("[s0_1]atrim=start=5.5:duration=0.428571,asetpts=PTS-STARTPTS,apad=whole_dur=0.428571[t0_1];", args);
        Assert.Contains("[t0_0][t0_1]concat=n=2:v=0:a=1[c0];[c0]anull[a]\"", args);
        Assert.Contains("-map 0:v -map \"[a]\" -c:a aac -b:a 192k -c:v copy -movflags +faststart", args);
        Assert.DoesNotContain("amix", args);
    }

    [Fact]
    public void TwoTracksAreMixedWithoutNormalization()
    {
        string args = Assemble(new List<string> { "C:\\Temp\\ton.wav", "C:\\Temp\\mikro.wav" });

        Assert.Contains("-i \"C:\\Temp\\ton.wav\" -i \"C:\\Temp\\mikro.wav\"", args);
        Assert.Contains("[2:a]asplit=2[s1_0][s1_1];", args);
        Assert.Contains("concat=n=2:v=0:a=1[c1];", args);
        Assert.Contains("[c0][c1]amix=inputs=2:normalize=0:duration=longest[a]", args);
    }

    [Fact]
    public void NumbersStayInvariantOnGermanWindows()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string args = Assemble(new List<string> { "C:\\Temp\\ton.wav" });

            Assert.Contains("start=5.5:", args);
            Assert.DoesNotContain("5,5", args);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
