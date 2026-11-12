using System.Text.RegularExpressions;

namespace MosaikCapture.Engine.Tests.Core;

// Crash protection from Core: error log, background threads, restart guard, Shell.Open.
public sealed class ErrorLogTests : IDisposable
{
    static string LogDirectory => AppDomain.CurrentDomain.BaseDirectory;
    static string LogFile => Path.Combine(LogDirectory, ErrorLog.FileName);
    static string OldFile => Path.Combine(LogDirectory, ErrorLog.OldFileName);

    public ErrorLogTests() => Clear();

    public void Dispose() => Clear();

    static void Clear()
    {
        File.Delete(LogFile);
        File.Delete(OldFile);
    }

    static string Log() => File.Exists(LogFile) ? File.ReadAllText(LogFile) : "";

    [Fact]
    public void WritesPlaceAndException()
    {
        ErrorLog.Write("Test", new InvalidOperationException("eins"));

        Assert.Contains("Test: System.InvalidOperationException: eins", Log());
    }

    [Fact]
    public void WritesAPlaceholderWithoutPlaceOrException()
    {
        ErrorLog.Write(null, null);
        ErrorLog.Write("nur Ort", null);

        string log = Log();
        Assert.Contains("unknown error", log);
        Assert.Contains("  nur Ort\r\n", log);
    }

    [Fact]
    public void RotatesTheLogBeyondOneMegabyte()
    {
        File.WriteAllText(LogFile, new string('x', 1100000));

        ErrorLog.Write("Test", new Exception("eins"));

        Assert.True(File.Exists(OldFile));
        Assert.Equal(1100000, new FileInfo(OldFile).Length);
        Assert.Contains("Test: System.Exception: eins", Log());
        Assert.True(new FileInfo(LogFile).Length < 2000, "the new log should only hold the new entry");
    }

    [Fact]
    public void ConcurrentWritersLoseNoEntries()
    {
        var threads = Enumerable.Range(0, 8).Select(i => Shell.RunInBackground("Schreiber", () =>
        {
            for (int k = 0; k < 50; k++) ErrorLog.Write("gleichzeitig " + i + "-" + k, null);
        })).ToList();
        threads.ForEach(t => t.Join());

        var lines = File.ReadAllLines(LogFile).Where(l => l.Contains("gleichzeitig ")).ToList();
        Assert.Equal(400, lines.Count);
        Assert.Equal(400, lines.Select(l => l.Substring(l.IndexOf("gleichzeitig", StringComparison.Ordinal))).Distinct().Count());
    }

    [Fact]
    public void BackgroundThreadFailureIsLoggedInsteadOfCrashing()
    {
        Shell.RunInBackground("Testfaden", () => throw new InvalidOperationException("Faden")).Join();

        Assert.Contains("Testfaden: System.InvalidOperationException: Faden", Log());
    }

    [Fact]
    public void BackgroundThreadCanBeSta()
    {
        ApartmentState state = ApartmentState.Unknown;
        Shell.RunInBackground("sta", () => state = Thread.CurrentThread.GetApartmentState(), sta: true).Join();

        Assert.Equal(ApartmentState.STA, state);
    }

    [Fact]
    public void OpeningAMissingFileReturnsFalseAndLogs()
    {
        Assert.False(Shell.Open(@"C:\gibt-es-nicht\datei.exe"));
        Assert.Contains(@"Opening C:\gibt-es-nicht\datei.exe", Log());
    }

    [Fact]
    public void NoRestartWhenCrashingInTheFirstMinute()
    {
        DateTime started = Program.started;
        int restarting = Program.restarting;
        try
        {
            Program.started = DateTime.Now;
            Program.restarting = 0;

            Program.Restart();
            Program.Restart();   // the guard makes a second crash report a no-op

            Assert.Single(Regex.Matches(Log(), "No restart: crashed within the first minute"));
            Assert.Equal(1, Program.restarting);
        }
        finally
        {
            Program.started = started;
            Program.restarting = restarting;
        }
    }
}
