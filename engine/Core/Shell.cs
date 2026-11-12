namespace MosaikCapture.Engine.Core;

// Small helpers for threads and the Windows shell; failures end up in the error log.
static class Shell
{
    // Background thread whose failures are logged instead of terminating the process.
    public static Thread RunInBackground(string name, ThreadStart body, bool sta = false)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { ErrorLog.Write(name, ex); }
        })
        { IsBackground = true, Name = name };
        if (sta) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }

    // Opens a file, folder, URL or program; on failure the reason is logged.
    public static bool Open(string file, string? args = null)
    {
        try
        {
            using (args == null ? Process.Start(file) : Process.Start(file, args)) { }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Oeffnen von " + file, ex);
            return false;
        }
    }

    // Opens Explorer with the file selected.
    public static void Reveal(string path) => Open("explorer.exe", "/select,\"" + path + "\"");
}
