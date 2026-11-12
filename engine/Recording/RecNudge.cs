namespace MosaikCapture.Engine.Recording;

// Desktop Duplication delivers the first frame only once something on the monitor changes; until then
// ffmpeg waits (and does not even react to "q"). If the screen is static, a tiny, almost
// invisible window (2 x 2 pixels, opacity 1/255) triggers the first frame. It runs on its own thread with its own
// message loop so it is independent of everything else.
static class RecNudge
{
    public static void Poke(int x, int y)
    {
        Shell.RunInBackground("Aufnahme anstossen", () => Run(x, y));
    }

    static void Run(int x, int y)
    {
        // WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | TOPMOST | NOACTIVATE; WS_POPUP
        IntPtr h = CreateWindowEx(0x00080000 | 0x20 | 0x80 | 0x8 | 0x08000000, "STATIC", "", 0x80000000u, x, y, 2, 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (h == IntPtr.Zero) return;
        SetLayeredWindowAttributes(h, 0, 1, 2);   // LWA_ALPHA
        for (int i = 0; i < 12; i++)
        {
            ShowWindow(h, i % 2 == 0 ? 4 : 0);   // SW_SHOWNOACTIVATE / SW_HIDE
            Pump(120);
        }
        DestroyWindow(h);
    }

    static void Pump(int ms)
    {
        var end = Environment.TickCount + ms;
        while (unchecked(end - Environment.TickCount) > 0)
        {
            while (PeekMessage(out MSG m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
}
