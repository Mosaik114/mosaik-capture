using Microsoft.Win32;

namespace MosaikCapture.Engine.Core;

/// <summary>
/// Look of all windows: the theme colors (warm cream or warm dark gray, turquoise accent) as
/// resources that follow the Windows light/dark setting, plus the shared fonts and fixed brushes.
/// The accent comes in two roles: <see cref="Accent"/> for lines, icons, bars and frames (dark
/// turquoise on cream, bright turquoise on dark gray, both at least 3:1 against the background) and
/// <see cref="AccentFill"/> for surfaces that carry light text or icons (at least 4.5:1 with
/// <see cref="Light"/> in both themes).
/// </summary>
static class Palette
{
    // ---------- Resource keys (theme colors) ----------

    public const string Background = "Bg";
    public const string Selected = "Sel";
    public const string Hover = "Hover";
    public const string Line = "Line";
    public const string Text = "Text";
    public const string Muted = "Muted";
    public const string Accent = "Accent";
    public const string Chip = "Chip";
    public const string AccentSoft = "AccentSoft";
    /// <summary>Accent surface under light text or icons (primary buttons, trim handles).</summary>
    public const string AccentFill = "AccentFill";
    /// <summary><see cref="AccentFill"/> while hovered or pressed: darker, so the text stays readable.</summary>
    public const string AccentFillHover = "AccentFillHover";

    // ---------- Fonts ----------

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily DigitFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily TitleFont = new("Georgia");

    // ---------- Fixed brushes (same in light and dark) ----------

    /// <summary>Recording red: the toolbar dot, the stop button and "off" icons.</summary>
    public static readonly SolidColorBrush RecordingRed = Frozen(0xFF, 0xF0, 0x80, 0x6A);
    /// <summary>Light foreground on dark or accent surfaces.</summary>
    public static readonly SolidColorBrush Light = Frozen(0xFF, 0xFA, 0xF9, 0xF5);
    /// <summary>Dimming outside the selected area.</summary>
    public static readonly SolidColorBrush Dimming = Frozen(0xA6, 0x14, 0x14, 0x13);
    /// <summary>Darkened chip on top of an image (size, duration, play button).</summary>
    public static readonly SolidColorBrush ImageChip = Frozen(0xD9, 0x26, 0x26, 0x24);
    /// <summary>Translucent accent filling the circle of a left click in recordings.</summary>
    public static readonly SolidColorBrush ClickFill = Frozen(0x40, 0x0E, 0x8C, 0x80);

    internal static bool? Force;   // tests only: light/dark regardless of the Windows setting

    static readonly string[] Keys = { Background, Selected, Hover, Line, Text, Muted, Accent, Chip, AccentFill, AccentFillHover };
    static readonly string[] LightColors = { "#FAF9F5", "#EEECE3", "#F4F2EA", "#E3E1D7", "#141413", "#7A786F", "#0E8C80", "#E6E3D8", "#0B7F74", "#096A61" };
    static readonly string[] DarkColors = { "#262624", "#35342F", "#2E2D2A", "#3A3935", "#FAF9F5", "#A3A197", "#2AC8A6", "#42413C", "#0B7F74", "#096A61" };

    public static SolidColorBrush Frozen(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    /// <summary>Puts the theme colors matching the Windows app theme into <paramref name="resources"/>.</summary>
    public static void Apply(ResourceDictionary resources) => Apply(resources, Force ?? WindowsUsesLightTheme());

    public static void Apply(ResourceDictionary resources, bool light)
    {
        string[] colors = light ? LightColors : DarkColors;
        for (int i = 0; i < Keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            resources[Keys[i]] = brush;
        }
        // accent as a subtle fill (background of the muted microphone icon)
        var accent = ((SolidColorBrush)resources[Accent]).Color;
        resources[AccentSoft] = Frozen(light ? (byte)0x26 : (byte)0x2E, accent.R, accent.G, accent.B);
    }

    static bool WindowsUsesLightTheme()
    {
        try
        {
            object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
            return value is int light && light != 0;
        }
        catch (System.Security.SecurityException) { return false; }   // setting not readable: stay dark
        catch (IOException) { return false; }
    }
}
