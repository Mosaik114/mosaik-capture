using System.ComponentModel;
namespace MosaikCapture.Engine.Core;

/// <summary>
/// Settings shared with the host: a Unicode INI file, section [capture], written by the host.
/// <c>MOSAIK_CAPTURE_TEST_CONFIG</c> points to a different file (tests and QA).
/// </summary>
internal static class CaptureSettings
{
    internal const string TestConfigVariable = "MOSAIK_CAPTURE_TEST_CONFIG";
    internal const string HostWindowClass = "MosaikCapture.Host";
    internal const int SaveAudioMessage = 0x8014;

    /// <summary>%LOCALAPPDATA%\MosaikCapture: settings and temporary recording files.</summary>
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaikCapture");

    /// <summary>Work folders of running recordings (video segments and audio tracks).</summary>
    public static readonly string RecordingWorkRoot = Path.Combine(Home, "recording");

    /// <summary>Where versions before 0.2 kept their work folders; cleaned up once.</summary>
    public static readonly string LegacyRecordingWorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyHomeName, LegacyWorkFolderName);

    // Folder names of older versions (which used German names); only read to clean up their leftovers.
    const string LegacyHomeName = "Mosaiks-Aufnahme", LegacyWorkFolderName = "Aufnahme";

    public static string FileName => Environment.GetEnvironmentVariable(TestConfigVariable) ?? Path.Combine(Home, "settings.ini");

    public static string ScreenshotFolder => Read("screenshot_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), UiText.Files.ScreenshotFolder));
    public static string RecordingFolder => Read("recording_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), UiText.Files.RecordingFolder));
    public static bool Microphone => Number("microphone", 0) != 0;
    public static bool SystemAudio => Number("system_audio", 1) != 0;
    public static bool Clicks => Number("show_clicks", 1) != 0;
    public static int Fps => NormalizeFps(Number("fps", 60));
    public static int Countdown => NormalizeCountdown(Number("countdown", 3));

    /// <summary>Only 30 and 60 fps are supported; anything else means 60.</summary>
    internal static int NormalizeFps(int fps) => fps == 30 ? 30 : 60;

    /// <summary>Only 0, 3 and 5 seconds are supported; anything else means 3.</summary>
    internal static int NormalizeCountdown(int seconds) => seconds is 0 or 5 ? seconds : 3;

    /// <summary>
    /// The host owns the INI file: audio toggles changed in the toolbar are sent to it as bit flags
    /// (see <see cref="AudioFlags"/>) and it persists them.
    /// </summary>
    public static void SaveAudio(bool microphone, bool sound, bool clicks)
    {
        IntPtr host = NativeMethods.FindWindow(HostWindowClass, null);
        if (host == IntPtr.Zero) return;   // no host (tests, or started by hand): nothing to persist
        if (!NativeMethods.PostMessage(host, SaveAudioMessage, new IntPtr(AudioFlags(microphone, sound, clicks)), IntPtr.Zero))
            ErrorLog.Write("Sending the audio settings to the host", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    /// <summary>1 = microphone, 2 = system sound, 4 = show clicks.</summary>
    internal static int AudioFlags(bool microphone, bool sound, bool clicks) => (microphone ? 1 : 0) | (sound ? 2 : 0) | (clicks ? 4 : 0);

    static string Read(string key, string fallback)
    {
        var value = new StringBuilder(2048);
        _ = NativeMethods.GetPrivateProfileString("capture", key, fallback, value, value.Capacity, FileName);
        return value.ToString();
    }

    static int Number(string key, int fallback) =>
        int.TryParse(Read(key, fallback.ToString(CultureInfo.InvariantCulture)), out int value) ? value : fallback;
}
