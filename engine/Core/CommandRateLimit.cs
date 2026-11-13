namespace MosaikCapture.Engine.Core;

/// <summary>
/// At most <c>limit</c> commands per sliding second; more are ignored. Protects against a process that
/// floods the engine's window with capture commands (each would otherwise open the overlay again).
/// Used on the UI thread only.
/// </summary>
internal sealed class CommandRateLimit
{
    readonly int limit;
    readonly TimeSpan window;
    readonly Queue<long> accepted = new();

    public CommandRateLimit(int limit, TimeSpan window)
    {
        this.limit = limit;
        this.window = window;
    }

    /// <summary>True if a command at <paramref name="nowTicks"/> (Stopwatch ticks) may run; it then counts.</summary>
    public bool Allow(long nowTicks)
    {
        long windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (accepted.Count > 0 && nowTicks - accepted.Peek() >= windowTicks) accepted.Dequeue();
        if (accepted.Count >= limit) return false;
        accepted.Enqueue(nowTicks);
        return true;
    }
}

/// <summary>
/// At most one log line per interval for an event that may repeat in bursts (rejected commands during a
/// flood), so the flood does not fill the error log as well; the next line says how many were left out.
/// Used on the UI thread only.
/// </summary>
internal sealed class LogThrottle
{
    readonly TimeSpan interval;
    long lastLogged;
    bool anyLogged;
    int skipped;

    public LogThrottle(TimeSpan interval) => this.interval = interval;

    /// <summary>
    /// True if the event at <paramref name="nowTicks"/> (Stopwatch ticks) should be logged; then
    /// <paramref name="skippedBefore"/> is the number of events left out since the last logged one.
    /// </summary>
    public bool Allow(long nowTicks, out int skippedBefore)
    {
        skippedBefore = 0;
        if (anyLogged && nowTicks - lastLogged < (long)(interval.TotalSeconds * Stopwatch.Frequency))
        {
            skipped++;
            return false;
        }
        anyLogged = true;
        lastLogged = nowTicks;
        skippedBefore = skipped;
        skipped = 0;
        return true;
    }
}
