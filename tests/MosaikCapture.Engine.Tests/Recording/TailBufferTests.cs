namespace MosaikCapture.Engine.Tests.Recording;

// ffmpeg prints the actual error last, so its output is kept from the end.
public sealed class TailBufferTests
{
    [Fact]
    public void KeepsTheNewestLinesWithinTheLimit()
    {
        var tail = new TailBuffer(20);
        for (int i = 0; i < 100; i++) tail.Add("line " + i);

        Assert.EndsWith("line 99", tail.ToString());
        Assert.DoesNotContain("line 0\r\n", tail.ToString());
        Assert.True(tail.ToString().Length <= 20);
    }

    [Fact]
    public void AnOverlongLastLineIsStillKept()
    {
        var tail = new TailBuffer(10);
        tail.Add("short");
        tail.Add(new string('x', 50));

        Assert.Equal(new string('x', 50), tail.ToString());
    }

    [Fact]
    public void StartsEmpty()
    {
        var tail = new TailBuffer(10);
        Assert.True(tail.IsEmpty);
        Assert.Equal("", tail.ToString());

        tail.Add("a");
        Assert.False(tail.IsEmpty);
    }

    [Fact]
    public void ConcurrentWritersLoseNoRecentLine()
    {
        var tail = new TailBuffer(100_000);
        Parallel.For(0, 1000, i => tail.Add("line " + i));

        Assert.Equal(1000, tail.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None).Length);
    }
}
