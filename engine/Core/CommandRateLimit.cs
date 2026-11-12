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
