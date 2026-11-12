namespace MosaikCapture.Engine.Core;

internal static class TaskExtensions
{
    /// <summary>For fire-and-forget tasks: writes a failure to the error log instead of losing it.</summary>
    public static void LogFailures(this Task task, string what) =>
        task.ContinueWith(t => ErrorLog.Write(what, t.Exception), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// For UI commands started from an event handler: a failure is logged and <paramref name="tellUser"/> runs
    /// on the calling (UI) thread, instead of an async void method crashing into the global handler.
    /// </summary>
    public static void OnFailure(this Task task, string what, Action tellUser)
    {
        var scheduler = SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
        task.ContinueWith(t =>
        {
            ErrorLog.Write(what, t.Exception);
            try { tellUser(); }
            catch (Exception ex) { ErrorLog.Write("Reporting a failed command", ex); }   // the window itself is broken: logged is all we can do
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, scheduler);
    }
}
