using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

// ---------- Gemeinsames fuer die Fenster ----------

static class RecUi
{
    public static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly SolidColorBrush Red = ShotOverlay.Frozen(0xFF, 0xF0, 0x80, 0x6A);   // wie im Ring und bei der Soundbar
    public static readonly SolidColorBrush Light = ShotOverlay.Frozen(0xFF, 0xFA, 0xF9, 0xF5);
    public const int NoActivate = 0x08000000, Tool = 0x80, ClickThrough = 0x20;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static void Setup(Window w, string title)
    {
        w.Title = title;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;
        w.Topmost = true;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.ResizeMode = ResizeMode.NoResize;
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        Palette.Apply(w.Resources);
    }

    public static IntPtr Handle(Window w, int exStyle)
    {
        IntPtr h = new WindowInteropHelper(w).EnsureHandle();
        if (exStyle != 0) SetWindowLong(h, -20, GetWindowLong(h, -20) | exStyle);
        return h;
    }

    // Im Video unsichtbar (WDA_EXCLUDEFROMCAPTURE), auf dem Bildschirm normal zu sehen
    public static void Exclude(IntPtr h) { SetWindowDisplayAffinity(h, 0x11); }

    public static double Scale(IntPtr h)
    {
        var s = HwndSource.FromHwnd(h);
        return s != null && s.CompositionTarget != null ? s.CompositionTarget.TransformToDevice.M11 : 1;
    }

    public static void Move(IntPtr h, int x, int y) { SetWindowPos(h, (IntPtr)(-1), x, y, 0, 0, 0x11); }                // TOPMOST, NOSIZE|NOACTIVATE
    public static void Bounds(IntPtr h, int x, int y, int w, int hh) { SetWindowPos(h, (IntPtr)(-1), x, y, w, hh, 0x10); }  // TOPMOST, NOACTIVATE

    public static ShotOverlay.RECT WorkAtCursor()
    {
        POINT p;
        GetCursorPos(out p);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(MonitorFromPoint(p, 2), ref mi);
        return mi.rcWork;
    }

    public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    public static SolidColorBrush Ref(FrameworkElement e, string key) { return e.TryFindResource(key) as SolidColorBrush; }

    // 0:07 bzw. 1:02:07
    public static string Clock(double sec)
    {
        int t = (int)Math.Floor(Math.Max(0, sec));
        return t >= 3600 ? (t / 3600) + ":" + (t / 60 % 60).ToString("00") + ":" + (t % 60).ToString("00") : (t / 60) + ":" + (t % 60).ToString("00");
    }

    // 0:07,4 (Zehntelsekunden)
    public static string Fine(double sec)
    {
        int d = (int)Math.Round(Math.Max(0, sec) * 10);
        return Clock(d / 10) + "," + (d % 10);
    }

    public static string Bytes(long n)
    {
        if (n < 1024 * 1024) return Math.Max(1, n / 1024) + " KB";
        double mb = n / 1048576.0;
        return mb.ToString(mb < 100 ? "0.0" : "0", De) + " MB";
    }

    public static string Num(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }

    // Zahl aus "name=wert"-Zeilen von ffprobe (erstes Vorkommen)
    public static double Field(string text, string name)
    {
        foreach (string l in text.Split('\n'))
        {
            string t = l.Trim();
            double v;
            if (t.StartsWith(name + "=") && double.TryParse(t.Substring(name.Length + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        }
        return 0;
    }

    public static string LastLine(string text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length > 0 ? lines[lines.Length - 1] : "Unbekannter Fehler";
    }

    public static BitmapSource LoadImage(string file)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(File.ReadAllBytes(file));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static TextBlock Glyph(string g, double size)
    {
        return new TextBlock
        {
            Text = g, FontSize = size, FontFamily = Icons,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static TextBlock Text(double size, string brush)
    {
        var t = new TextBlock { FontSize = size, FontFamily = Ui, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur, double depth, double opacity)
    {
        return new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black };
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public ShotOverlay.RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
