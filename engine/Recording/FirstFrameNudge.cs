using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Desktop Duplication delivers the first frame only once something on the monitor changes; until
/// then ffmpeg waits (and does not even react to "q"). If the screen is static, a tiny, almost
/// invisible window (2 x 2 pixels, opacity 1/255) blinking a few times triggers the first frame.
/// It runs on its own thread with its own message loop, independent of everything else.
/// </summary>
internal static class FirstFrameNudge
{
    const int Blinks = 12, BlinkMilliseconds = 120;

    public static void Poke(int x, int y) => Shell.RunInBackground("Nudging the first frame", () => Blink(x, y));

    static void Blink(int x, int y)
    {
        const int Styles = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW |
                           NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE;
        IntPtr window = NativeMethods.CreateWindowEx(Styles, "STATIC", "", NativeMethods.WS_POPUP, x, y, 2, 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            ErrorLog.Write("Creating the first-frame window", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        try
        {
            NativeMethods.SetLayeredWindowAttributes(window, 0, 1, NativeMethods.LWA_ALPHA);
            for (int i = 0; i < Blinks; i++)
            {
                NativeMethods.ShowWindow(window, i % 2 == 0 ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);
                PumpMessages(BlinkMilliseconds);
            }
        }
        finally { NativeMethods.DestroyWindow(window); }
    }

    static void PumpMessages(int milliseconds)
    {
        int end = Environment.TickCount + milliseconds;
        while (unchecked(end - Environment.TickCount) > 0)
        {
            while (NativeMethods.PeekMessage(out var message, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            Thread.Sleep(10);
        }
    }
}
