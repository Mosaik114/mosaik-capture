using Microsoft.Win32;

namespace MosaikCapture.Engine.Core;

// Color palette: warm cream or warm dark gray, terracotta as the accent.
static class Palette
{
    internal static bool? Force;   // tests only: light/dark regardless of the Windows setting

    public static void Apply(ResourceDictionary r)
    {
        if (Force.HasValue) { Apply(r, Force.Value); return; }
        bool light = false;
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
            light = v is int value && value != 0;
        }
        catch (Exception) { }   // setting not readable: stay dark
        Apply(r, light);
    }

    public static void Apply(ResourceDictionary r, bool light)
    {
        string[] keys = { "Bg", "Sel", "Hover", "Line", "Text", "Muted", "Accent", "Chip" };
        string[] colors = light
            ? new[] { "#FAF9F5", "#EEECE3", "#F4F2EA", "#E3E1D7", "#141413", "#7A786F", "#D97757", "#E6E3D8" }
            : new[] { "#262624", "#35342F", "#2E2D2A", "#3A3935", "#FAF9F5", "#A3A197", "#D97757", "#42413C" };
        for (int i = 0; i < keys.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            b.Freeze();
            r[keys[i]] = b;
        }
        // accent as a subtle fill (background of the muted microphone icon)
        var tint = new SolidColorBrush(Color.FromArgb(light ? (byte)0x26 : (byte)0x2E, 0xD9, 0x77, 0x57));
        tint.Freeze();
        r["AccentSoft"] = tint;
    }
}
