namespace MosaikCapture.Engine.Core;

internal static class TaskExtensions
{
    /// <summary>For fire-and-forget tasks: writes a failure to the error log instead of losing it.</summary>
    public static void LogFailures(this Task task, string what) =>
        task.ContinueWith(t => ErrorLog.Write(what, t.Exception), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
