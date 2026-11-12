namespace MosaikCapture.Engine.Tests.Core;

// The clipboard writer's own behaviour, with fake writes (the real clipboard of the machine is left alone).
public sealed class ClipboardWriterTests
{
    [Fact]
    public async Task WriteRunsOnAnStaThreadOffTheCaller()
    {
        ApartmentState apartment = ApartmentState.Unknown;
        int caller = Environment.CurrentManagedThreadId, writer = caller;

        bool copied = await ClipboardWriter.SetAsync("test", () =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            writer = Environment.CurrentManagedThreadId;
        });

        Assert.True(copied);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(caller, writer);
    }

    [Fact]
    public async Task FailedWriteReportsFalseInsteadOfThrowing()
    {
        bool copied = await ClipboardWriter.SetAsync("test", () => throw new System.Runtime.InteropServices.COMException("clipboard busy"));

        Assert.False(copied);
    }

    [Fact]
    public async Task SlowWriteGivesUpAfterTheTimeout()
    {
        using var release = new ManualResetEventSlim();
        var watch = Stopwatch.StartNew();

        bool copied = await ClipboardWriter.SetAsync("test", () => release.Wait(TimeSpan.FromSeconds(30)));
        release.Set();

        Assert.False(copied);
        Assert.InRange(watch.Elapsed.TotalSeconds, ClipboardWriter.Timeout.TotalSeconds - 0.5, ClipboardWriter.Timeout.TotalSeconds + 5);
    }
}
