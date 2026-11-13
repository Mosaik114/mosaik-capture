namespace MosaikCapture.Engine.Diagnostics;

/// <summary>
/// <c>--live-test &lt;dir&gt;</c>: end-to-end check on a real, interactive desktop. Shows an animated test
/// window, takes a screenshot of it and records it for 3 s with ffmpeg; writes live-report.json and
/// sets the exit code. Needs an interactive desktop (Desktop Duplication does not work on hidden ones).
/// </summary>
internal static class LiveTest
{
    static readonly TimeSpan RecordingLength = TimeSpan.FromSeconds(3);

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        string ini = Path.Combine(directory, "live-test.ini");
        File.WriteAllText(ini, "[capture]\r\nfps=30\r\ncountdown=0\r\nmicrophone=0\r\nsystem_audio=0\r\nshow_clicks=0\r\n", Encoding.Unicode);
        CaptureSettings.UseFile(ini);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var (window, animation) = CreateTestWindow();
        RecordingSession? session = null;

        void Fail(Exception error)
        {
            WriteReport(directory, "{\"passed\":false,\"error\":\"" + JsonText(error.Message) + "\"}");
            Environment.ExitCode = 1;
            session?.Discard();
            app.Shutdown();
        }

        async void RecordAndCheck()
        {
            try
            {
                var area = CaptureWindowArea(window, out RECT monitor);
                CheckScreenshot(directory, area);
                if (Dxgi.Find(monitor, out int output, out RECT desktop) != OutputLookup.Found) throw new InvalidOperationException("The test monitor is not available to Desktop Duplication");
                session = new RecordingSession(area, output, desktop, new AudioSettings(false, false, false), CaptureSettings.Load().Fps);
                session.StartAudio();
                await session.ResumeAsync();
                await Task.Delay(RecordingLength);
                string video = Path.Combine(directory, "live-test.mp4");
                var finished = session;
                var result = await finished.FinishAsync(video, CancellationToken.None) ?? throw new InvalidOperationException(finished.Reason);
                if (result.Width != area.Width || result.Height != area.Height || result.Duration < 1)
                    throw new InvalidOperationException("The recording has unexpected dimensions or length");
                WriteReport(directory, "{\"passed\":true,\"screenshot\":true,\"video\":true,\"width\":" + result.Width + ",\"height\":" + result.Height +
                    ",\"duration\":" + result.Duration.ToString(CultureInfo.InvariantCulture) + ",\"audio\":" + (result.Audio ? "true" : "false") + "}");
                animation.Stop();
                window.Close();
                app.Shutdown();
            }
            catch (Exception error) { Fail(error); }   // any failure fails the test
        }

        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Fail(e.Exception); };
        window.Show();
        animation.Start();
        var begin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        begin.Tick += delegate { begin.Stop(); RecordAndCheck(); };
        begin.Start();
        app.Run();
    }

    // A dark window with a moving turquoise square, so the video has changing content.
    static (Window Window, DispatcherTimer Animation) CreateTestWindow()
    {
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(23, 58, 58)) };
        canvas.Children.Add(new TextBlock { Text = "Mosaik Capture · live test", Foreground = Brushes.White, FontSize = 30, Margin = new Thickness(30) });
        var marker = new Shapes.Rectangle { Width = 100, Height = 100, Fill = new SolidColorBrush(Color.FromRgb(42, 200, 166)) };
        Canvas.SetTop(marker, 150);
        canvas.Children.Add(marker);
        var window = new Window
        {
            Title = "Mosaik Capture live test",
            Content = canvas,
            Width = 640,
            Height = 360,
            Left = 80,
            Top = 80,
            Topmost = true,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        double x = 0;
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        animation.Tick += delegate { x = (x + 9) % 500; Canvas.SetLeft(marker, x + 20); };
        return (window, animation);
    }

    static Int32Rect CaptureWindowArea(Window window, out RECT monitor)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info))
            throw new InvalidOperationException("The test monitor was not found");
        monitor = info.rcMonitor;
        Point origin = window.PointToScreen(new Point(0, 0));
        var toDevice = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
        var area = new Int32Rect((int)origin.X, (int)origin.Y, (int)(window.ActualWidth * toDevice.M11), (int)(window.ActualHeight * toDevice.M22));
        return RecordingArea.Fit(area, monitor);
    }

    static void CheckScreenshot(string directory, Int32Rect area)
    {
        var bitmap = ScreenGrabber.Capture(area.X, area.Y, area.Width, area.Height, 96);
        File.WriteAllBytes(Path.Combine(directory, "live-test.png"), Screenshots.Encode(bitmap, false));
        if (bitmap.PixelWidth != area.Width || bitmap.PixelHeight != area.Height) throw new InvalidOperationException("The screenshot has unexpected dimensions");
    }

    static void WriteReport(string directory, string json) => File.WriteAllText(Path.Combine(directory, "live-report.json"), json, Encoding.UTF8);

    static string JsonText(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
