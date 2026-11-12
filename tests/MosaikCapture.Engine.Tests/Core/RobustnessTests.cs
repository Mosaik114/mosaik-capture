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
        ["native crash"] = new SEHException("access violation"),
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
    [InlineData("native crash", false)]
    public void OnlyOutsideWorldFailuresAreSurvivable(string sample, bool recoverable) =>
        Assert.Equal(recoverable, Program.IsRecoverable(Samples[sample]));

    [Fact]
    public void UptimeCountsFromTheStart()
    {
        long started = Program.StartTimestamp;
        try
        {
            Program.StartTimestamp = Stopwatch.GetTimestamp() - 90 * Stopwatch.Frequency;
            Assert.InRange(Program.Uptime.TotalSeconds, 89.9, 95);
        }
        finally { Program.StartTimestamp = started; }
    }

    [Fact]
    public void RecoverableFailuresKeepTheEngineWithoutRestarting()
    {
        bool restarted = false;

        Assert.Equal(Program.UiFailureResponse.Continue, Program.ResponseTo(new IOException(), () => restarted = true));
        Assert.False(restarted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // within the first minute no restart happens, but the engine still ends
    public void BugsEndTheEngineWhetherOrNotItRestarted(bool restartStarted)
    {
        int restarts = 0;

        var response = Program.ResponseTo(new NullReferenceException(), () => { restarts++; return restartStarted; });

        Assert.Equal(Program.UiFailureResponse.Shutdown, response);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void WaveFormatIsThePacked40ByteStructure()
    {
        Assert.Equal(40, Marshal.SizeOf<Wasapi.WAVEFORMATEXTENSIBLE>());
        Assert.Equal(0, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("FormatTag"));
        Assert.Equal(2, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("Channels"));
        Assert.Equal(4, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("SamplesPerSecond"));
        Assert.Equal(8, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("AverageBytesPerSecond"));
        Assert.Equal(12, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("BlockAlign"));
        Assert.Equal(14, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("BitsPerSample"));
        Assert.Equal(16, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("ExtensionSize"));
        Assert.Equal(18, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("ValidBitsPerSample"));
        Assert.Equal(20, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("ChannelMask"));
        Assert.Equal(24, (int)Marshal.OffsetOf<Wasapi.WAVEFORMATEXTENSIBLE>("SubFormat"));
    }

    [Fact]
    public void WaveFormatDescribesStereoFloat()
    {
        IntPtr memory = Wasapi.AllocateFloatStereoFormat(48000);
        try
        {
            Assert.Equal(unchecked((short)0xFFFE), Marshal.ReadInt16(memory, 0));
            Assert.Equal(2, Marshal.ReadInt16(memory, 2));
            Assert.Equal(48000, Marshal.ReadInt32(memory, 4));
            Assert.Equal(48000 * 8, Marshal.ReadInt32(memory, 8));
            Assert.Equal(8, Marshal.ReadInt16(memory, 12));
            Assert.Equal(32, Marshal.ReadInt16(memory, 14));
            Assert.Equal(22, Marshal.ReadInt16(memory, 16));
            Assert.Equal(32, Marshal.ReadInt16(memory, 18));
            Assert.Equal(3, Marshal.ReadInt32(memory, 20));
            var subFormat = new byte[16];
            Marshal.Copy(memory + 24, subFormat, 0, 16);
            Assert.Equal(new Guid("00000003-0000-0010-8000-00aa00389b71"), new Guid(subFormat));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

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
