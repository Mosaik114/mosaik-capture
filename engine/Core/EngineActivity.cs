namespace MosaikCapture.Engine.Core;

/// <summary>
/// Whether quitting the engine now would lose the user's work: one answer for the host's quit command
/// and for its state query. Busy while a recording is in any phase (picking, countdown, recording, saving)
/// or any <see cref="EngineWork"/> runs: the screenshot overlay or editor is open, a screenshot is being
/// saved, the trim window is open or an export runs. Thread-safe (exports end on pool threads).
/// </summary>
internal static class EngineActivity
{
    static int running;

    /// <summary>Pieces of <see cref="EngineWork"/> running now.</summary>
    public static int Running => Volatile.Read(ref running);

    public static bool Busy => Running > 0 || ScreenRecorder.Busy;

    /// <summary>Work that runs from now until <see cref="EngineWork.End"/> is called on the result.</summary>
    public static EngineWork Start()
    {
        var work = new EngineWork();
        work.Begin();
        return work;
    }

    internal static void Enter() => Interlocked.Increment(ref running);

    internal static void Exit() => Interlocked.Decrement(ref running);
}

/// <summary>
/// One piece of work counted by <see cref="EngineActivity"/>, e.g. a window that is open. Begin and End may
/// be called any number of times: it counts once while begun, so a window hidden twice cannot unbalance the count.
/// </summary>
internal sealed class EngineWork
{
    int active;

    public void Begin()
    {
        if (Interlocked.Exchange(ref active, 1) == 0) EngineActivity.Enter();
    }

    public void End()
    {
        if (Interlocked.Exchange(ref active, 0) == 1) EngineActivity.Exit();
    }
}
