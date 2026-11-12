namespace MosaikCapture.Engine.Core;

/// <summary>Shared setup and building blocks of the borderless tool windows (cards, toolbar, frame, countdown).</summary>
internal static class ToolWindows
{
    /// <summary>Borderless, transparent, topmost, never activated, themed.</summary>
    public static void Setup(Window window, string title)
    {
        window.Title = title;
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.ResizeMode = ResizeMode.NoResize;
        window.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        Palette.Apply(window.Resources);
    }

    /// <summary>Creates the (hidden) window handle and adds extended styles (WS_EX_*).</summary>
    public static IntPtr Handle(Window window, int extendedStyles)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (extendedStyles != 0) NativeMethods.AddExtendedStyle(hwnd, extendedStyles);
        return hwnd;
    }

    /// <summary>Invisible in the video (and in screenshots of other apps), visible on screen as usual.</summary>
    public static void ExcludeFromCapture(IntPtr hwnd) => NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Device pixels per WPF unit of the monitor the window is on.</summary>
    public static double Scale(IntPtr hwnd)
    {
        var source = HwndSource.FromHwnd(hwnd);
        return source?.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
    }

    public static bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Loads an image completely into memory, so the file can be deleted right away.</summary>
    public static BitmapSource LoadImage(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(File.ReadAllBytes(file));
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontSize = size,
        FontFamily = Palette.IconFont,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static TextBlock Text(double size, string brushKey)
    {
        var text = new TextBlock { FontSize = size, FontFamily = Palette.UiFont, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    /// <summary>A small rounded label in the theme colors (sizes, hints, feedback).</summary>
    public static Border Chip(UIElement content)
    {
        var chip = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 4), BorderThickness = new Thickness(1), Child = content };
        chip.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        chip.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return chip;
    }

    /// <summary>Activates the window even if Windows would refuse to hand over the focus.</summary>
    public static void TakeFocus(Window window, IntPtr hwnd)
    {
        window.Activate();
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // briefly attach to the input thread of the active window
            uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint ownThread = NativeMethods.GetCurrentThreadId();
            NativeMethods.AttachThreadInput(ownThread, foregroundThread, true);
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.AttachThreadInput(ownThread, foregroundThread, false);
        }
        window.Focus();
        Keyboard.Focus(window);
    }

    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur, double depth, double opacity) =>
        new() { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black };
}

/// <summary>How times and sizes are shown to the user (German number format).</summary>
internal static class Formats
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>0:07 or 1:02:07</summary>
    public static string Clock(double seconds)
    {
        int total = (int)Math.Floor(Math.Max(0, seconds));
        string minutesAndSeconds = (total / 60 % 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        return total >= 3600 ? total / 3600 + ":" + minutesAndSeconds : total / 60 + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>0:07,4 (tenths of a second)</summary>
    public static string ClockWithTenths(double seconds)
    {
        int tenths = (int)Math.Round(Math.Max(0, seconds) * 10);
        return Clock(tenths / 10) + "," + tenths % 10;
    }

    /// <summary>KB below 1 MB, above that MB with a German decimal comma.</summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024 * 1024) return Math.Max(1, bytes / 1024) + " KB";
        double megabytes = bytes / 1048576.0;
        return megabytes.ToString(megabytes < 100 ? "0.0" : "0", German) + " MB";
    }
}
