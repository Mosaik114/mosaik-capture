namespace MosaikCapture.Engine.Diagnostics;

/// <summary>
/// <c>--self-test &lt;report&gt;</c>: quick smoke test used by the build. Writes {"passed":n,"failed":0}
/// (or the failing check) as JSON and sets the exit code; shows no UI.
/// </summary>
internal static class SelfTest
{
    static int passed;

    public static void Run(string report)
    {
        passed = 0;
        try
        {
            CheckSettings(Path.GetFullPath(report) + ".ini");
            CheckRecordingArea();
            Check(!Ffmpeg.IsLocalPath(".\\ffmpeg") && !Ffmpeg.IsLocalPath("\\\\server\\tools") && Ffmpeg.IsLocalPath("C:\\Programme\\ffmpeg"), "safe ffmpeg paths");
            CheckPng();
            Check(!ScreenRecorder.Busy && !ScreenRecorder.Live, "recording idle at start");
            File.WriteAllText(report, "{\"passed\":" + passed + ",\"failed\":0}", Encoding.UTF8);
        }
        catch (InvalidOperationException failure)
        {
            File.WriteAllText(report, "{\"passed\":" + passed + ",\"failed\":1,\"error\":\"" + failure.Message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}", Encoding.UTF8);
            Environment.ExitCode = 1;
        }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++;
    }

    static void CheckSettings(string ini)
    {
        Environment.SetEnvironmentVariable(CaptureSettings.TestConfigVariable, ini);
        File.WriteAllText(ini, "[capture]\r\nscreenshot_folder=C:\\Bilder\\Grüße\r\nrecording_folder=C:\\Videos\\Aufnahmen\r\nmicrophone=1\r\nsystem_audio=0\r\nshow_clicks=0\r\nfps=30\r\ncountdown=5\r\n", Encoding.Unicode);
        Check(CaptureSettings.ScreenshotFolder == "C:\\Bilder\\Grüße", "Unicode screenshot folder");
        Check(CaptureSettings.Microphone && !CaptureSettings.SystemAudio && !CaptureSettings.Clicks, "audio settings");
        Check(CaptureSettings.Fps == 30 && CaptureSettings.Countdown == 5, "frame rate and countdown");
    }

    static void CheckRecordingArea()
    {
        var monitor = new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
        Check(RecordingArea.Fit(new Int32Rect(101, 51, 301, 201), monitor) == new Int32Rect(101, 51, 300, 200), "even video dimensions");
        var fitted = RecordingArea.Fit(new Int32Rect(1800, 1000, 400, 300), monitor);
        Check(fitted.X + fitted.Width <= 1920 && fitted.Y + fitted.Height <= 1080, "monitor bounds");
    }

    static void CheckPng()
    {
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        byte[] png = Screenshots.Encode(image, false);
        var decoded = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(decoded.PixelWidth == 2 && decoded.PixelHeight == 2, "PNG dimensions");
        byte[] actual = new byte[16];
        decoded.CopyPixels(actual, 8, 0);
        Check(actual[2] == 255 && actual[5] == 255, "PNG colors");
    }
}
