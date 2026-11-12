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

    public static long Now100Ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}
