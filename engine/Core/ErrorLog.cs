namespace MosaikCapture.Engine.Core;

/// <summary>
/// Error log in %LOCALAPPDATA%\MosaikCapture\logs\error.log (the exe's folder may be read-only, e.g.
/// below Program Files). Beyond 1 MB it is renamed to error.old.log and started afresh.
/// Writing never throws: if the log cannot be written, all the engine can do is keep running.
/// </summary>
internal static class ErrorLog
{
    internal const string FileName = "error.log", OldFileName = "error.old.log";
    const long MaxBytes = 1_000_000;
    static readonly object gate = new();
    static string directory = Path.Combine(CaptureSettings.Home, "logs");

    /// <summary>The folder of the log; created on the first write. Tests point it at a temporary folder.</summary>
    internal static string Directory
    {
        get { lock (gate) return directory; }
        set { lock (gate) directory = value; }
    }

    public static void Write(Exception? exception) => Write(null, exception);

    /// <param name="context">What the engine was doing, e.g. "Saving a screenshot".</param>
    /// <param name="exception">The failure, if any.</param>
    public static void Write(string? context, Exception? exception)
    {
        lock (gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, FileName);
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    string old = Path.Combine(directory, OldFileName);
                    File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Format(context, exception) + "\r\n\r\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    internal static string Format(string? context, Exception? exception) =>
        context == null ? exception?.ToString() ?? "unknown error" : context + (exception == null ? "" : ": " + exception);
}
