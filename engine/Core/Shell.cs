using System.ComponentModel;

namespace MosaikCapture.Engine.Core;

/// <summary>Small helpers for threads and the Windows shell; failures end up in the error log.</summary>
internal static class Shell
{
    /// <summary>A background thread whose failures are logged instead of terminating the process.</summary>
    public static Thread RunInBackground(string name, ThreadStart body, bool sta = false)
    {
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { ErrorLog.Write(name, ex); }   // a background helper must never crash the engine
        })
        { IsBackground = true, Name = name };
        if (sta) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>Opens a file, folder, URL or program; on failure the reason is logged.</summary>
    public static bool Open(string file, string? arguments = null)
    {
        try
        {
            using (arguments == null ? Process.Start(file) : Process.Start(file, arguments)) { }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ErrorLog.Write("Opening " + file, ex);
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string path) => Open("explorer.exe", "/select,\"" + path + "\"");
}
