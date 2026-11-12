namespace MosaikCapture.Engine.Tests.Recording;

// Ffmpeg.Run never waits forever: a hanging process is killed on timeout or cancellation.
// ping.exe stands in for a hanging ffmpeg (no ffmpeg needed, no window).
public sealed class FfmpegRunTests
{
    static readonly string Ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");

    /// <summary>Runs about 30 s unless killed.</summary>
    static FfmpegArgs Hang() => new FfmpegArgs().Add("-n", "30", "127.0.0.1");

    [Fact]
    public async Task HangingProcessIsKilledAfterTheTimeout()
    {
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => Ffmpeg.RunAsync(Ping, Hang(), TimeSpan.FromMilliseconds(300), cancellation: TestContext.Current.CancellationToken));

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.2, 10);
        Assert.Equal(UiText.Common.FfmpegTimedOut, error.Message);
    }

    [Fact]
    public async Task CancellationKillsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ffmpeg.RunAsync(Ping, Hang(), TimeSpan.FromMinutes(1), cancellation: cancellation.Token));

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.2, 10);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotStartAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ffmpeg.RunAsync("C:\\does-not-exist.exe", Hang(), TimeSpan.FromMinutes(1), cancellation: cancellation.Token));
    }

    [Fact]
    public async Task QuickProcessReturnsItsExitCodeAndOutput()
    {
        var result = await Ffmpeg.RunAsync(Ping, new FfmpegArgs().Add("-n", "1", "127.0.0.1"), TimeSpan.FromSeconds(30), cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("127.0.0.1", result.Output);
    }

    [Fact]
    public void ExitIsReportedOnlyAfterTheLastErrorLine()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        for (int round = 0; round < 20; round++)
        {
            var errors = new TailBuffer(1000);
            using var exited = new ManualResetEventSlim();
            string seenAtExit = "";
            using var process = Ffmpeg.Start(cmd, new FfmpegArgs().Add("/d", "/c", "echo first 1>&2 & echo last-line 1>&2"), null, errors.Add, () =>
            {
                seenAtExit = errors.ToString();
                exited.Set();
            });

            Assert.True(exited.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "no exit reported");
            Assert.Contains("last-line", seenAtExit);
        }
    }

    [Fact]
    public async Task WaitingForAProcessHoldsNoThread()
    {
        ThreadPool.GetAvailableThreads(out int before, out _);
        var runs = Enumerable.Range(0, 4).Select(_ => Ffmpeg.RunAsync(Ping, new FfmpegArgs().Add("-n", "2", "127.0.0.1"), TimeSpan.FromSeconds(30),
            cancellation: TestContext.Current.CancellationToken)).ToList();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ThreadPool.GetAvailableThreads(out int during, out _);

        await Task.WhenAll(runs);
        Assert.True(before - during < 4, "each running ffmpeg should not occupy a pool thread");
    }

    [Fact]
    public void PreviewImagesFailQuietlyWhenFfmpegCannotRun()
    {
        var locator = Ffmpeg.Locator;
        try
        {
            Ffmpeg.Locator = new MissingFfmpeg();

            Assert.Null(RecordedVideo.Frame("C:\\missing.mp4", 1, 640));
            Assert.Null(RecordedVideo.Strip("C:\\missing.mp4", 6, 3, 60));
        }
        finally { Ffmpeg.Locator = locator; }
    }

    /// <summary>Points at an ffmpeg that does not exist, so starting it throws Win32Exception.</summary>
    sealed class MissingFfmpeg : IFfmpegLocator
    {
        public string? Ffmpeg => "C:\\does-not-exist\\ffmpeg.exe";
        public string? Probe => "C:\\does-not-exist\\ffprobe.exe";
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(60, 720)]       // a minute of video may take ten minutes plus the base
    [InlineData(-5, 120)]
    public void EncodingTimeoutGrowsWithTheMedia(double seconds, double expected) => Assert.Equal(expected, FfmpegTimeouts.ForMedia(seconds).TotalSeconds);
}
