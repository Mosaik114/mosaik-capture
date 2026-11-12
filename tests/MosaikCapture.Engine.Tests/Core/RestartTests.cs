namespace MosaikCapture.Engine.Tests.Core;

// A crashed engine restarts itself; the new process must not race the old one for the single-instance mutex.
public sealed class RestartTests
{
    static string UniqueMutexName() => "Local\\MosaikCapture.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void RestartPassesTheOldProcessId() => Assert.Equal("--idle --after 4242", Program.RestartArguments(4242));

    [Theory]
    [InlineData(new[] { "--idle", "--after", "4242" }, 4242)]
    [InlineData(new[] { "--after", "17", "--recording" }, 17)]
    [InlineData(new[] { "--idle" }, null)]
    [InlineData(new[] { "--idle", "--after" }, null)]
    [InlineData(new[] { "--idle", "--after", "-3" }, null)]
    [InlineData(new[] { "--idle", "--after", "x" }, null)]
    public void ParentProcessIdIsOptional(string[] args, int? expected) => Assert.Equal(expected, CaptureBridge.ParseParent(args));

    [Fact]
    public void RestartedEngineStillStartsIdle() => Assert.Equal(0, CaptureBridge.ParseCommand(new[] { "--idle", "--after", "4242" }));

    [Fact]
    public void WaitingForAProcessThatIsGoneReturnsAtOnce()
    {
        var watch = Stopwatch.StartNew();

        Assert.True(CaptureBridge.WaitForExit(int.MaxValue - 1, TimeSpan.FromSeconds(10)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WaitingForARunningProcessTimesOut()
    {
        using var self = Process.GetCurrentProcess();

        Assert.False(CaptureBridge.WaitForExit(self.Id, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void FreeMutexIsTakenAndAHeldOneIsNot()
    {
        string name = UniqueMutexName();
        using var first = new Mutex(false, name);
        using var second = new Mutex(false, name);
        Assert.True(CaptureBridge.TryAcquire(first));
        try
        {
            bool taken = false;
            var other = new Thread(() => taken = CaptureBridge.TryAcquire(second));   // mutexes are owned per thread
            other.Start();
            other.Join();
            Assert.False(taken);
        }
        finally { first.ReleaseMutex(); }
    }

    [Fact]
    public void AbandonedMutexOfACrashedOwnerCountsAsTaken()
    {
        string name = UniqueMutexName();
        var crashed = new Thread(() =>
        {
            var owned = new Mutex(true, name);   // exits without releasing, like a crashed engine
            GC.KeepAlive(owned);
        });
        crashed.Start();
        crashed.Join();

        using var mutex = new Mutex(false, name);
        Assert.True(CaptureBridge.TryAcquire(mutex));
        mutex.ReleaseMutex();
    }
}
