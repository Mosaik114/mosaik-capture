namespace MosaikCapture.Engine.Core;

// Error log next to the exe; beyond 1 MB it is renamed to Fehler.alt.log and started afresh.
// The German file names are kept on purpose: the host and existing installations rely on them.
static class ErrorLog
{
    internal const string FileName = "Fehler.log", OldFileName = "Fehler.alt.log";
    const long MaxBytes = 1000000;
    static readonly object gate = new();

    public static void Write(Exception? ex) => Write(null, ex);

    public static void Write(string? where, Exception? ex)
    {
        lock (gate)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string file = Path.Combine(dir, FileName);
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    string old = Path.Combine(dir, OldFileName);
                    File.Delete(old);
                    File.Move(file, old);
                }
                string text = where == null ? (ex == null ? "unbekannter Fehler" : ex.ToString())
                                            : where + (ex == null ? "" : ": " + ex);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + "  " + text + "\r\n\r\n");
            }
            catch (Exception) { }   // the log itself is not writable: all we can do is keep running
        }
    }
}
