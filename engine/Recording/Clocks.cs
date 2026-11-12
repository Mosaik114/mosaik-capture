namespace MosaikCapture.Engine.Recording;

/// <summary>
/// The two clocks that keep video and audio in sync: wall-clock microseconds since 1970 (what
/// ffmpeg's RTCSTART uses) and 100 ns ticks of the performance counter (what WASAPI timestamps use).
/// </summary>
internal static class Clocks
{
    const long UnixEpochAsFileTime = 116444736000000000L;

    public static long EpochMicros()
    {
        NativeMethods.GetSystemTimePreciseAsFileTime(out long fileTime);
        return (fileTime - UnixEpochAsFileTime) / 10;
    }

    /// <summary>The performance counter in 100 ns ticks, in exact integer math (no rounding drift over hours).</summary>
    public static long Now100Ns() => To100Ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal static long To100Ns(long counter, long frequency)
    {
        const long TicksPerSecond = 10_000_000;
        long seconds = counter / frequency, rest = counter % frequency;
        return seconds * TicksPerSecond + rest * TicksPerSecond / frequency;   // rest < frequency: no overflow
    }
}
