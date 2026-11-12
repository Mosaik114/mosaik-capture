namespace MosaikCapture.Engine.Tests.Recording;

// ffmpeg sees every recording, so only fully qualified local directories may provide it.
public sealed class FfmpegLocatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("ffmpeg")]
    [InlineData(@".\ffmpeg")]
    [InlineData(@"C:ffmpeg")]
    [InlineData(@"\\server\freigabe")]
    [InlineData(@"\ffmpeg")]
    [InlineData("C")]
    [InlineData("1:\\tools")]
    public void RelativeAndNetworkPathsAreRejected(string directory) => Assert.False(Ffmpeg.IsLocalPath(directory));

    [Theory]
    [InlineData(@"C:\ffmpeg\bin")]
    [InlineData("D:/tools")]
    [InlineData(@"z:\")]
    public void FullyQualifiedLocalPathsAreAccepted(string directory) => Assert.True(Ffmpeg.IsLocalPath(directory));

    [Fact]
    public void FindSkipsRelativeEntriesEvenIfTheyContainFfmpeg()
    {
        using var work = new TempDirectory();
        string foreign = Directory.CreateDirectory(work.File("fremd")).FullName;
        string real = Directory.CreateDirectory(work.File("echt")).FullName;
        File.WriteAllText(Path.Combine(foreign, "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(real, "ffmpeg.exe"), "");
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = work.Path;

            Assert.Equal(Path.Combine(real, "ffmpeg.exe"), Ffmpeg.Find("ffmpeg.exe", new[] { "fremd", @".\fremd", "\"" + real + "\"" }));
            Assert.Null(Ffmpeg.Find("ffmpeg.exe", new[] { "fremd", null, "  ", "C:\\a\"b" }));
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public void FindTakesTheFirstMatchInOrder()
    {
        using var work = new TempDirectory();
        string first = Directory.CreateDirectory(work.File("a")).FullName;
        string second = Directory.CreateDirectory(work.File("b")).FullName;
        File.WriteAllText(Path.Combine(first, "ffprobe.exe"), "");
        File.WriteAllText(Path.Combine(second, "ffprobe.exe"), "");

        Assert.Equal(Path.Combine(first, "ffprobe.exe"), Ffmpeg.Find("ffprobe.exe", new[] { work.File("leer"), first, second }));
    }

    [Fact]
    public void FindIgnoresInvalidPathEntries()
    {
        using var work = new TempDirectory();
        File.WriteAllText(work.File("ffmpeg.exe"), "");

        Assert.Equal(work.File("ffmpeg.exe"), Ffmpeg.Find("ffmpeg.exe", new[] { "C:\\in<valid>|path", work.Path }));
    }

    [Fact]
    public void FixedInstallLocationsComeBeforePath()
    {
        var locations = Ffmpeg.SearchLocations().ToList();

        Assert.EndsWith(@"Microsoft\WinGet\Links", locations[0]);
        Assert.EndsWith(@"ffmpeg\bin", locations[1]);
        Assert.EndsWith(@"chocolatey\bin", locations[2]);
        Assert.EndsWith(@"scoop\shims", locations[3]);
        Assert.Equal((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'), locations.Skip(4));
    }

    [Fact]
    public void DetectedFfmpegIsLocal()
    {
        Assert.SkipWhen(Ffmpeg.Exe == null, "ffmpeg is not installed");

        Assert.True(Ffmpeg.IsLocalPath(Path.GetDirectoryName(Ffmpeg.Exe)!), "found ffmpeg should be local: " + Ffmpeg.Exe);
        Assert.Equal("ffmpeg.exe", Path.GetFileName(Ffmpeg.Exe), ignoreCase: true);
    }

    [Fact]
    public async Task EncoderDetectionRunsOnceAndAlwaysYieldsAnEncoder()
    {
        var first = Ffmpeg.DetectEncoderAsync();

        Assert.Same(first, Ffmpeg.DetectEncoderAsync());
        var finished = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(finished == first, "encoder detection hangs");
        var encoder = await first;
        Assert.True(Enum.IsDefined(typeof(VideoEncoder), encoder));
        if (Ffmpeg.Exe == null) Assert.Equal(VideoEncoder.Software, encoder);
    }
}
