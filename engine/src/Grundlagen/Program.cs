// Eigene Schnellsuche: Strg+Leertaste oeffnet ein Suchfeld fuer
// Apps, Dateien (Windows-Suchindex), Rechnungen und Websuche.
// Bauen mit build.ps1 (nutzt den in Windows enthaltenen C#-Compiler, C# 5).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

static class Program
{
    const string RestartArg = "--idle";
    static readonly DateTime started = DateTime.Now;
    static readonly object logLock = new object();
    static int restarting;

    [STAThread]
    static void Main(string[] args) { CaptureBridge.Run(args); }

    internal static void Protect(Application app)
    {
        // Ein einzelner Fehler (etwa nach dem Standby, wenn ein Geraet fehlt) soll nur
        // im Protokoll landen und nicht die ganze Suche samt Ring und Anzeigen beenden
        app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (s, e) => { Log(e.Exception); e.SetObserved(); };
        // Nicht abfangbar (Fehler ausserhalb der Oberflaeche): protokollieren und frisch neu starten
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log("Absturz", e.ExceptionObject as Exception);
            if (e.IsTerminating) Restart();
        };
    }

    internal static bool WaitFor(Mutex mutex)
    {
        try { return mutex.WaitOne(20000); }
        catch (AbandonedMutexException) { return true; }   // alte Instanz ist weg, die Sperre gehoert jetzt uns
    }

    // Nur wenn die Suche schon eine Weile lief, sonst droht eine Absturzschleife gleich beim Start
    internal static void Restart()
    {
        if (Interlocked.Exchange(ref restarting, 1) == 1) return;
        if (DateTime.Now - started < TimeSpan.FromMinutes(1)) { Log("Kein Neustart: Absturz in der ersten Minute", null); return; }
        Open(Assembly.GetEntryAssembly().Location, RestartArg);
    }

    public static void Log(Exception ex) { Log(null, ex); }

    // Fehlerprotokoll neben der Exe; ab 1 MB wird es zu Fehler.alt.log und neu begonnen
    public static void Log(string where, Exception ex)
    {
        lock (logLock)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string file = System.IO.Path.Combine(dir, "Fehler.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1000000)
                {
                    string old = System.IO.Path.Combine(dir, "Fehler.alt.log");
                    File.Delete(old);
                    File.Move(file, old);
                }
                string text = where == null ? (ex == null ? "unbekannter Fehler" : ex.ToString())
                                            : where + (ex == null ? "" : ": " + ex);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n\r\n");
            }
            catch (Exception) { }   // Protokoll selbst nicht schreibbar: bleibt nur, weiterzulaufen
        }
    }

    // Hintergrund-Thread, dessen Fehler im Protokoll landen, statt die ganze Suche zu beenden
    public static Thread Background(string name, ThreadStart body, bool sta = false)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { Log(name, ex); }
        }) { IsBackground = true, Name = name };
        if (sta) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }

    // Datei, Ordner, Adresse oder Programm oeffnen; klappt es nicht, steht der Grund im Protokoll
    public static bool Open(string file, string args = null)
    {
        try
        {
            using (args == null ? Process.Start(file) : Process.Start(file, args)) { }
            return true;
        }
        catch (Exception ex)
        {
            Log("Oeffnen von " + file, ex);
            return false;
        }
    }

    // Explorer mit markierter Datei
    public static void Reveal(string path)
    {
        Open("explorer.exe", "/select,\"" + path + "\"");
    }
}
