namespace MosaikCapture.Engine.Tests.Recording;

// Ffmpeg.Run never waits forever: a hanging process is killed on timeout or cancellation.
// ping.exe stands in for a hanging ffmpeg (no ffmpeg needed, no window).
public sealed class FfmpegRunTests
{
    static readonly string Ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");

    /// <summary>Runs about 30 s unless killed.</summary>
    static FfmpegArgs Hang() => new FfmpegArgs().Add("-n", "30", "127.0.0.1");

    [Fact]
    public void HangingProcessIsKilledAfterTheTimeout()
    {
        var watch = Stopwatch.StartNew();

        var error = Assert.Throws<TimeoutException>(() => Ffmpeg.Run(Ping, Hang(), TimeSpan.FromMilliseconds(300), out _, out _, cancellation: TestContext.Current.CancellationToken));

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.2, 10);
        Assert.Equal(UiText.Common.FfmpegTimedOut, error.Message);
    }

    [Fact]
    public void CancellationKillsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => Ffmpeg.Run(Ping, Hang(), TimeSpan.FromMinutes(1), out _, out _, cancellation: cancellation.Token));

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.2, 10);
    }

    [Fact]
    public void AlreadyCancelledDoesNotStartAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Ffmpeg.Run("C:\\does-not-exist.exe", Hang(), TimeSpan.FromMinutes(1), out _, out _, cancellation: cancellation.Token));
    }

    [Fact]
    public void QuickProcessReturnsItsExitCodeAndOutput()
    {
        int code = Ffmpeg.Run(Ping, new FfmpegArgs().Add("-n", "1", "127.0.0.1"), TimeSpan.FromSeconds(30), out string output, out _, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("127.0.0.1", output);
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(60, 720)]       // a minute of video may take ten minutes plus the base
    [InlineData(-5, 120)]
    public void EncodingTimeoutGrowsWithTheMedia(double seconds, double expected) => Assert.Equal(expected, FfmpegTimeouts.ForMedia(seconds).TotalSeconds);
}
