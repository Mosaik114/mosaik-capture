namespace MosaikCapture.Engine.Core;

// Settings shared with the Rust host: a Unicode INI file, section [capture].
// MOSAIK_CAPTURE_TEST_CONFIG points to a different file (tests and QA).
static class CaptureSettings
{
    internal const string TestConfigVariable = "MOSAIK_CAPTURE_TEST_CONFIG";
    internal const string HostWindowClass = "MosaiksAufnahmeRust";
    internal const int SaveAudioMessage = 0x8014;

    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaiks-Aufnahme");

    public static string FileName => Environment.GetEnvironmentVariable(TestConfigVariable) ?? Path.Combine(Home, "settings.ini");

    public static string ScreenshotFolder => Read("screenshot_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mosaiks-Screenshots"));
    public static string RecordingFolder => Read("recording_folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mosaiks-Aufnahmen"));
    public static bool Microphone => Number("microphone", 0) != 0;
    public static bool SystemAudio => Number("system_audio", 1) != 0;
    public static bool Clicks => Number("show_clicks", 1) != 0;
    public static int Fps => NormalizeFps(Number("fps", 60));
    public static int Countdown => NormalizeCountdown(Number("countdown", 3));

    // Only 30 and 60 fps are supported; anything else means 60.
    internal static int NormalizeFps(int fps) => fps == 30 ? 30 : 60;

    // Only 0, 3 and 5 seconds are supported; anything else means 3.
    internal static int NormalizeCountdown(int seconds) => seconds == 0 || seconds == 5 ? seconds : 3;

    // The host owns the INI file: audio toggles changed in the toolbar are sent to it as bit flags
    // (1 = microphone, 2 = system sound, 4 = show clicks) and it persists them.
    public static void SaveAudio(bool mic, bool sound, bool clicks)
    {
        IntPtr host = FindWindow(HostWindowClass, null);
        if (host != IntPtr.Zero) PostMessage(host, SaveAudioMessage, new IntPtr(AudioFlags(mic, sound, clicks)), IntPtr.Zero);
    }

    internal static int AudioFlags(bool mic, bool sound, bool clicks) => (mic ? 1 : 0) | (sound ? 2 : 0) | (clicks ? 4 : 0);

    static string Read(string key, string fallback)
    {
        var value = new StringBuilder(2048);
        _ = GetPrivateProfileString("capture", key, fallback, value, value.Capacity, FileName);
        return value.ToString();
    }

    static int Number(string key, int fallback) =>
        int.TryParse(Read(key, fallback.ToString(CultureInfo.CurrentCulture)), out int value) ? value : fallback;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, int size, string path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
}
