namespace MosaikCapture.Engine.Core;

/// <summary>
/// Test isolation (<c>MOSAIK_CAPTURE_TEST_INSTANCE</c>), the engine's half of the host's <c>src/isolation.rs</c>;
/// both follow <c>contract/contract.json</c>. When set, every name another process could meet (mutex, window
/// title, the host's window class) gets the suffix ".Test.&lt;instance&gt;", which no real name can carry, and the
/// engine keeps its data (logs, recording work folders) below %TEMP%\MosaikCapture.Test.&lt;instance&gt; instead
/// of %LOCALAPPDATA%. So a test engine can neither reach, block nor clean up after a real one.
/// </summary>
/// <remarks>
/// Only an isolated engine accepts <see cref="SimulateBusyMessage"/> (wParam 1 = busy, 0 = idle). It only sets
/// <see cref="SimulatedBusy"/>, which <see cref="EngineActivity.Busy"/> includes: the bridge itself then reports busy
/// and refuses quit, so the host's cross-process test checks the real quit path without a capture.
/// </remarks>
internal static class TestIsolation
{
    internal const string InstanceVariable = "MOSAIK_CAPTURE_TEST_INSTANCE";
    internal const string Infix = ".Test.";
    internal const int SimulateBusyMessage = 0x8040;

    static int simulatedBusy;

    /// <summary>The sanitized instance name, or null when not isolated (the normal case).</summary>
    internal static readonly string? Instance = Sanitize(Environment.GetEnvironmentVariable(InstanceVariable));

    /// <summary>Stands in for %LOCALAPPDATA% when isolated.</summary>
    internal static readonly string? LocalAppData = Instance == null ? null : Path.Combine(Path.GetTempPath(), "MosaikCapture" + Infix + Instance);

    /// <summary>Keeps A-Z a-z 0-9 _ - and replaces every other UTF-16 unit with _, as the host does; empty means null.</summary>
    internal static string? Sanitize(string? instance) =>
        string.IsNullOrEmpty(instance) ? null : new string(instance!.Select(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '_').ToArray());

    internal static string Isolate(string name) => Isolate(name, Instance);

    internal static string Isolate(string name, string? instance) => instance == null ? name : name + Infix + instance;

    internal static bool SimulatedBusy => Volatile.Read(ref simulatedBusy) != 0;

    /// <summary>Installs <see cref="OnMessage"/> on the engine's window, only when isolated.</summary>
    internal static void Hook(HwndSource window)
    {
        if (Instance != null) window.AddHook(OnMessage);
    }

    /// <summary>Handles <see cref="SimulateBusyMessage"/> (replies 1); every other message goes on to the bridge.</summary>
    internal static IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != SimulateBusyMessage) return IntPtr.Zero;
        Volatile.Write(ref simulatedBusy, wParam != IntPtr.Zero ? 1 : 0);
        handled = true;
        return new IntPtr(1);
    }
}
