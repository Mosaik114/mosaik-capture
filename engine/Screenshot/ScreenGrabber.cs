namespace MosaikCapture.Engine.Screenshot;

/// <summary>Reads the screen: pixels, monitors and the visible windows.</summary>
internal static class ScreenGrabber
{
    const int MinWindowSize = 20;

    /// <summary>The bounding box of all monitors in device pixels.</summary>
    public static Int32Rect VirtualScreen() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN), NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Copies screen content via GDI into a frozen WPF bitmap (without the mouse pointer).
    /// Throws if Windows refuses (e.g. on a locked desktop).
    /// </summary>
    public static BitmapSource Capture(int x, int y, int width, int height, double dpi)
    {
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            memory = NativeMethods.CreateCompatibleDC(screen);
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Creating a " + width + " x " + height + " screen bitmap failed");
            IntPtr previous = NativeMethods.SelectObject(memory, bitmap);
            bool copied = NativeMethods.BitBlt(memory, 0, 0, width, height, screen, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            NativeMethods.SelectObject(memory, previous);
            if (!copied) ErrorLog.Write("Copying the screen", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var header = new NativeMethods.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[width * height * 4];
            if (NativeMethods.GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, NativeMethods.DIB_RGB_COLORS) == 0)
                ErrorLog.Write("Reading the screen bitmap failed", null);
            var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgr32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) NativeMethods.DeleteDC(memory);
            if (screen != IntPtr.Zero) _ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>All monitors with their work areas (same order in both lists).</summary>
    public static void Monitors(List<RECT> monitors, List<RECT> workAreas)
    {
        monitors.Clear();
        workAreas.Clear();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT bounds, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(info.rcMonitor);
                workAreas.Add(info.rcWork);
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Visible windows in z-order (topmost first) relative to the virtual screen, measured without
    /// their shadows; skips our own windows, tool windows, click-through overlays and the desktop.
    /// </summary>
    public static void VisibleWindows(Int32Rect virtualScreen, List<Rect> windows)
    {
        windows.Clear();
        uint ownProcess = (uint)Process.GetCurrentProcess().Id;
        var className = new StringBuilder(64);
        var screen = new Rect(0, 0, virtualScreen.Width, virtualScreen.Height);
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            int styles = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((styles & (NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW)) != 0) return true;
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
            if (process == ownProcess) return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // e.g. other virtual desktops
            className.Length = 0;
            _ = NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW") return true;   // desktop = whole monitor
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) != 0)
                NativeMethods.GetWindowRect(hwnd, out bounds);
            var area = new Rect(bounds.left - virtualScreen.X, bounds.top - virtualScreen.Y, Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));
            area.Intersect(screen);
            if (!area.IsEmpty && area.Width > MinWindowSize && area.Height > MinWindowSize) windows.Add(area);
            return true;
        }, IntPtr.Zero);
    }
}
