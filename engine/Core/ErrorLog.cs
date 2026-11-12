namespace MosaikCapture.Engine.Core;

/// <summary>
/// One error log file with size-based rotation: beyond 1 MB it is renamed to error.old.log and started
/// afresh. Writing never throws: if the log cannot be written, all the engine can do is keep running.
/// Thread-safe.
/// </summary>
internal sealed class ErrorLogFile
{
    internal const string FileName = "error.log", OldFileName = "error.old.log";
    const long MaxBytes = 1_000_000;
    readonly object gate = new();

    public ErrorLogFile(string directory) => Directory = directory;

    /// <summary>The folder of the log; created on the first write.</summary>
    public string Directory { get; }

    /// <param name="context">What the engine was doing, e.g. "Saving a screenshot".</param>
    /// <param name="exception">The failure, if any.</param>
    public void Write(string? context, Exception? exception)
    {
        lock (gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string file = Path.Combine(Directory, FileName);
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    string old = Path.Combine(Directory, OldFileName);
                    File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + ErrorLog.Format(context, exception) + "\r\n\r\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}

/// <summary>
/// The engine's error log, in %LOCALAPPDATA%\MosaikCapture\logs\error.log (the exe's folder may be
/// read-only, e.g. below Program Files). A static facade over one <see cref="ErrorLogFile"/>, because
/// every part of the engine logs; tests replace the file with one in a temporary folder.
/// </summary>
internal static class ErrorLog
{
    internal const string FileName = ErrorLogFile.FileName, OldFileName = ErrorLogFile.OldFileName;

    static volatile ErrorLogFile current = new(Path.Combine(CaptureSettings.Home, "logs"));

    /// <summary>The log in use.</summary>
    internal static ErrorLogFile Current
    {
        get => current;
        set => current = value;
    }

    /// <summary>The folder of the log in use (setting it switches to a new log file there).</summary>
    internal static string Directory
    {
        get => current.Directory;
        set => current = new ErrorLogFile(value);
    }

    public static void Write(Exception? exception) => Write(null, exception);

    /// <param name="context">What the engine was doing, e.g. "Saving a screenshot".</param>
    /// <param name="exception">The failure, if any.</param>
    public static void Write(string? context, Exception? exception) => current.Write(context, exception);

    internal static string Format(string? context, Exception? exception) =>
        context == null ? exception?.ToString() ?? "unknown error" : context + (exception == null ? "" : ": " + exception);
}
