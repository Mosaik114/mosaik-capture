using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MosaikCapture.Engine.Tests.Core;

// Small rules that keep the engine running: which UI exceptions are survivable, the command rate limit,
// exact clock conversion, the ffmpeg locator and independent log files.
public sealed class RobustnessTests
{
    static readonly Dictionary<string, Exception> Samples = new()
    {
        ["io"] = new IOException("disk"),
        ["access"] = new UnauthorizedAccessException(),
        ["com"] = new COMException("clipboard busy"),
        ["win32"] = new Win32Exception(5),
        ["external"] = new ExternalException("device"),
        ["timeout"] = new TimeoutException(),
        ["cancelled"] = new OperationCanceledException(),
        ["aggregate of recoverable"] = new AggregateException(new IOException(), new TimeoutException()),
        ["invocation of recoverable"] = new TargetInvocationException(new COMException()),
        ["null reference"] = new NullReferenceException(),
        ["invalid operation"] = new InvalidOperationException("bug"),
        ["argument"] = new ArgumentOutOfRangeException(),
        ["aggregate with a bug"] = new AggregateException(new IOException(), new NullReferenceException()),
        ["empty aggregate"] = new AggregateException(),
        ["empty invocation"] = new TargetInvocationException(null),
    };

    [Theory]
    [InlineData("io", true)]
    [InlineData("access", true)]
    [InlineData("com", true)]
    [InlineData("win32", true)]
    [InlineData("external", true)]
    [InlineData("timeout", true)]
    [InlineData("cancelled", true)]
    [InlineData("aggregate of recoverable", true)]
    [InlineData("invocation of recoverable", true)]
    [InlineData("null reference", false)]
    [InlineData("invalid operation", false)]
    [InlineData("argument", false)]
    [InlineData("aggregate with a bug", false)]
    [InlineData("empty aggregate", false)]
    [InlineData("empty invocation", false)]
    public void OnlyOutsideWorldFailuresAreSurvivable(string sample, bool recoverable) =>
        Assert.Equal(recoverable, Program.IsRecoverable(Samples[sample]));

    [Fact]
    public void RateLimitLetsTenCommandsPerSecondThrough()
    {
        var limit = new CommandRateLimit(10, TimeSpan.FromSeconds(1));
        long second = Stopwatch.Frequency;

        var first = Enumerable.Range(0, 15).Select(i => limit.Allow(i * second / 100)).ToList();   // 15 within 150 ms

        Assert.Equal(10, first.Count(allowed => allowed));
        Assert.All(first.Take(10), Assert.True);
        Assert.False(limit.Allow(second / 2));
        Assert.True(limit.Allow(second + second / 100));   // the oldest left the window
    }

    [Fact]
    public void RateLimitNeverBlocksAPerson()
    {
        var limit = new CommandRateLimit(CaptureBridge.CommandsPerSecond, TimeSpan.FromSeconds(1));

        for (int i = 0; i < 100; i++) Assert.True(limit.Allow(i * Stopwatch.Frequency / 4));   // four presses a second
    }

    [Theory]
    [InlineData(0L, 10_000_000L, 0L)]
    [InlineData(10_000_000L, 10_000_000L, 10_000_000L)]
    [InlineData(3_579_545L, 3_579_545L, 10_000_000L)]                       // a classic QPC frequency: exactly one second
    [InlineData(3_579_545L * 86_400 * 30 + 1, 3_579_545L, 25_920_000_000_002L)]  // 30 days, no floating point drift
    [InlineData(long.MaxValue / 2, 10_000_000L, long.MaxValue / 2)]
    public void PerformanceCounterConvertsExactly(long counter, long frequency, long expected) =>
        Assert.Equal(expected, Clocks.To100Ns(counter, frequency));

    [Fact]
    public void LocatorSearchesAgainWhenFfmpegDisappears()
    {
        using var root = new TempDirectory();
        string first = Directory.CreateDirectory(root.File("first")).FullName, second = Directory.CreateDirectory(root.File("second")).FullName;
        File.WriteAllText(Path.Combine(first, "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(first, "ffprobe.exe"), "");
        var locator = new FfmpegLocator(() => new[] { first, second });

        Assert.Equal(Path.Combine(first, "ffmpeg.exe"), locator.Ffmpeg);
        Assert.Equal(Path.Combine(first, "ffprobe.exe"), locator.Probe);

        File.Delete(Path.Combine(first, "ffmpeg.exe"));
        File.Delete(Path.Combine(first, "ffprobe.exe"));
        Assert.Null(locator.Ffmpeg);
        Assert.Null(locator.Probe);

        File.WriteAllText(Path.Combine(second, "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(second, "ffprobe.exe"), "");
        Assert.Equal(Path.Combine(second, "ffmpeg.exe"), locator.Ffmpeg);
        Assert.Equal(Path.Combine(second, "ffprobe.exe"), locator.Probe);
    }

    [Fact]
    public void LogFilesAreIndependent()
    {
        using var root = new TempDirectory();
        var one = new ErrorLogFile(root.File("one"));
        var two = new ErrorLogFile(root.File("two"));

        one.Write("only in one", null);

        Assert.Contains("only in one", File.ReadAllText(Path.Combine(one.Directory, ErrorLog.FileName)));
        Assert.False(File.Exists(Path.Combine(two.Directory, ErrorLog.FileName)));
    }
}
