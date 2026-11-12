namespace MosaikCapture.Engine.Diagnostics;

// `--self-test <report>`: quick smoke test used by the build script. Writes {"passed":n,"failed":0} (or the
// failing check) as JSON and sets the exit code; shows no UI.
static class SelfTest
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); passed++; }
    public static void Run(string report)
    {
        try
        {
            string ini = Path.GetFullPath(report) + ".ini";
            Environment.SetEnvironmentVariable(CaptureSettings.TestConfigVariable, ini);
            File.WriteAllText(ini, "[capture]\r\nscreenshot_folder=C:\\Bilder\\Grüße\r\nrecording_folder=C:\\Videos\\Aufnahmen\r\nmicrophone=1\r\nsystem_audio=0\r\nshow_clicks=0\r\nfps=30\r\ncountdown=5\r\n", Encoding.Unicode);
            Check(CaptureSettings.ScreenshotFolder == "C:\\Bilder\\Grüße", "Unicode-Speicherordner");
            Check(CaptureSettings.Microphone && !CaptureSettings.SystemAudio && !CaptureSettings.Clicks, "Audioeinstellungen");
            Check(CaptureSettings.Fps == 30 && CaptureSettings.Countdown == 5, "Bildrate und Vorlauf");
            var monitor = new ShotOverlay.RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
            Check(Rec.Fit(new Int32Rect(101, 51, 301, 201), monitor) == new Int32Rect(101, 51, 300, 200), "Gerade Videomasse");
            var fitted = Rec.Fit(new Int32Rect(1800, 1000, 400, 300), monitor);
            Check(fitted.X + fitted.Width <= 1920 && fitted.Y + fitted.Height <= 1080, "Monitorgrenzen");
            Check(!FF.IsLocalPath(".\\ffmpeg") && !FF.IsLocalPath("\\\\server\\tools") && FF.IsLocalPath("C:\\Programme\\ffmpeg"), "Sichere ffmpeg-Pfade");
            var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            byte[] png = Shot.Encode(image, false);
            var decoded = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoded.PixelWidth == 2 && decoded.PixelHeight == 2, "PNG-Abmessungen");
            byte[] actual = new byte[16]; decoded.CopyPixels(actual, 8, 0);
            Check(actual[2] == 255 && actual[5] == 255, "PNG-Farben");
            Check(!Rec.Busy && !Rec.Live, "Aufnahme initial inaktiv");
            File.WriteAllText(report, "{\"passed\":" + passed + ",\"failed\":0}", Encoding.UTF8);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, "{\"passed\":" + passed + ",\"failed\":1,\"error\":\"" + error.Message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}", Encoding.UTF8);
            Environment.ExitCode = 1;
        }
    }
}
