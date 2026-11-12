using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

static class CaptureLiveTests
{
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public ShotOverlay.RECT Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    static string Json(string text) { return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " "); }

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        string ini = Path.Combine(directory, "live-test.ini");
        File.WriteAllText(ini, "[capture]\r\nfps=30\r\ncountdown=0\r\nmicrophone=0\r\nsystem_audio=0\r\nshow_clicks=0\r\n", Encoding.Unicode);
        Environment.SetEnvironmentVariable("MOSAIK_CAPTURE_TEST_CONFIG", ini);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(23, 58, 58)) };
        canvas.Children.Add(new TextBlock { Text = "Mosaik · Funktionstest", Foreground = Brushes.White, FontSize = 30, Margin = new Thickness(30) });
        var marker = new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = new SolidColorBrush(Color.FromRgb(217, 119, 87)) };
        Canvas.SetTop(marker, 150);canvas.Children.Add(marker);
        var window = new Window { Title = "Mosaiks Aufnahme – Testbild", Content = canvas, Width = 640, Height = 360,
            Left = 80, Top = 80, Topmost = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        RecSession session = null;
        Action<Exception> fail = error =>
        {
            File.WriteAllText(Path.Combine(directory, "live-report.json"), "{\"passed\":false,\"error\":\"" + Json(error.Message) + "\"}", Encoding.UTF8);
            Environment.ExitCode = 1;
            if (session != null) { try { session.Discard(); } catch (Exception) { } }
            app.Shutdown();
        };
        app.DispatcherUnhandledException += (s, e) => { e.Handled = true; fail(e.Exception); };
        double x = 0;
        var animate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        animate.Tick += (s, e) => { x = (x + 9) % 500; Canvas.SetLeft(marker, x + 20); };
        window.Show();animate.Start();
        var begin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        begin.Tick += delegate
        {
            begin.Stop();
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                var mi = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) throw new Exception("Testmonitor nicht gefunden");
                Point p = window.PointToScreen(new Point(0, 0));
                var scale = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
                var r = Rec.Fit(new Int32Rect((int)p.X, (int)p.Y, (int)(window.ActualWidth * scale.M11), (int)(window.ActualHeight * scale.M22)), mi.Monitor);
                var bitmap = ShotOverlay.Grab(r.X, r.Y, r.Width, r.Height, 96);
                string shot = Path.Combine(directory, "testbild.png");
                File.WriteAllBytes(shot, Shot.Encode(bitmap, false));
                if (bitmap.PixelWidth != r.Width || bitmap.PixelHeight != r.Height) throw new Exception("Screenshot-Abmessungen stimmen nicht");
                int index;ShotOverlay.RECT desk;
                if (!Dxgi.Find(mi.Monitor, out index, out desk)) throw new Exception("Testmonitor wird von Desktop Duplication nicht gefunden");
                FF.Detect().Wait(6000);
                session = new RecSession(r, index, desk, false, false);
                session.Audio.Start();session.Resume();
                var finish = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                finish.Tick += delegate
                {
                    finish.Stop();
                    string video = Path.Combine(directory, "testaufnahme.mp4");
                    Task.Factory.StartNew(() => session.Finish(video)).ContinueWith(task =>
                    {
                        try
                        {
                            if (task.IsFaulted) throw task.Exception;
                            RecResult result = task.Result;
                            if (result == null) throw new Exception(session.Reason);
                            if (result.Width != r.Width || result.Height != r.Height || result.Duration < 1) throw new Exception("Aufnahme-Metadaten stimmen nicht");
                            File.WriteAllText(Path.Combine(directory, "live-report.json"), "{\"passed\":true,\"screenshot\":true,\"video\":true,\"width\":" + result.Width + ",\"height\":" + result.Height + ",\"duration\":" + result.Duration.ToString(CultureInfo.InvariantCulture) + ",\"audio\":" + (result.Audio ? "true" : "false") + "}", Encoding.UTF8);
                            animate.Stop();window.Close();app.Shutdown();
                        }
                        catch (Exception error) { fail(error); }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                };
                finish.Start();
            }
            catch (Exception error) { fail(error); }
        };
        begin.Start();app.Run();
    }
}
