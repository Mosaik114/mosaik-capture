namespace MosaikCapture.Engine.Recording;

// Clocks: microseconds since 1970 (for ffmpeg) and 100 ns ticks of the performance counter (like WASAPI).
static class RecClock
{
    public static long EpochUs()
    {
        GetSystemTimePreciseAsFileTime(out long ft);
        return (ft - 116444736000000000L) / 10;
    }

    public static long Now100() { return (long)(Stopwatch.GetTimestamp() * (10000000.0 / Stopwatch.Frequency)); }

    [DllImport("kernel32.dll")] static extern void GetSystemTimePreciseAsFileTime(out long ft);
}
