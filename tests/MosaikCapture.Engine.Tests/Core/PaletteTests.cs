namespace MosaikCapture.Engine.Tests.Core;

public sealed class PaletteTests
{
    static readonly string[] Keys = { "Bg", "Sel", "Hover", "Line", "Text", "Muted", "Accent", "Chip", "AccentSoft", "AccentFill", "AccentFillHover", "RecordingRed", "RecordingRedFill", "WarningSoft" };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefinesAllBrushesFrozen(bool light) => WpfThread.Run(() =>
    {
        var resources = new ResourceDictionary();
        Palette.Apply(resources, light);

        foreach (string key in Keys)
        {
            var brush = Assert.IsType<SolidColorBrush>(resources[key]);
            Assert.True(brush.IsFrozen, key + " should be frozen so other threads can use it");
        }
    });

    [Fact]
    public void LightAndDarkShareTheAccentFillButNotTheAccentOrBackground() => WpfThread.Run(() =>
    {
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        Palette.Apply(light, true);
        Palette.Apply(dark, false);

        Assert.Equal(System.Windows.Media.Color.FromRgb(0x0E, 0x8C, 0x80), Color(light, "Accent"));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x2A, 0xC8, 0xA6), Color(dark, "Accent"));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x0B, 0x7F, 0x74), Color(light, "AccentFill"));
        Assert.Equal(Color(light, "AccentFill"), Color(dark, "AccentFill"));
        Assert.Equal(Color(light, "AccentFillHover"), Color(dark, "AccentFillHover"));
        Assert.Equal(Color(light, "Accent").B, Color(light, "AccentSoft").B);
        Assert.Equal(Color(dark, "Accent").B, Color(dark, "AccentSoft").B);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xFA, 0xF9, 0xF5), Color(light, "Bg"));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x26, 0x26, 0x24), Color(dark, "Bg"));
        Assert.NotEqual(Color(light, "AccentSoft").A, Color(dark, "AccentSoft").A);
    });

    [Fact]
    public void ForceOverridesTheWindowsSetting() => WpfThread.Run(() =>
    {
        bool? previous = Palette.Force;
        try
        {
            var resources = new ResourceDictionary();
            Palette.Force = true;
            Palette.Apply(resources);
            Assert.Equal(System.Windows.Media.Color.FromRgb(0xFA, 0xF9, 0xF5), Color(resources, "Bg"));

            Palette.Force = false;
            Palette.Apply(resources);
            Assert.Equal(System.Windows.Media.Color.FromRgb(0x26, 0x26, 0x24), Color(resources, "Bg"));
        }
        finally { Palette.Force = previous; }
    });

    // WCAG 2 contrast: light text on accent fills needs 4.5:1, accent lines and icons 3:1 against the background.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AccentColorsAreReadable(bool light) => WpfThread.Run(() =>
    {
        var resources = new ResourceDictionary();
        Palette.Apply(resources, light);

        Assert.True(Contrast(Palette.Light.Color, Color(resources, "AccentFill")) >= 4.5);
        Assert.True(Contrast(Palette.Light.Color, Color(resources, "AccentFillHover")) >= 4.5);
        Assert.True(Contrast(Color(resources, "Accent"), Color(resources, "Bg")) >= 3);
        Assert.True(Contrast(Color(resources, "Accent"), Color(resources, "Sel")) >= 3);
    });

    // The recording red is a clear red, not orange: hue 350-360°. As text (export error) it needs
    // 4.5:1 against the background, as an icon 3:1 on hovered buttons and on its own warning chip;
    // the discard fill carries a light icon (3:1) and stands out from the bar (3:1).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingRedIsARedThatReads(bool light) => WpfThread.Run(() =>
    {
        var resources = new ResourceDictionary();
        Palette.Apply(resources, light);
        Color red = Color(resources, "RecordingRed"), fill = Color(resources, "RecordingRedFill");

        foreach (Color c in new[] { red, fill })
        {
            // red is the largest channel and blue a little above green: the hue leans away from orange
            Assert.True(c.R > c.B && c.B >= c.G, c + " leans towards orange");
            double hue = 360 - 60.0 * (c.B - c.G) / (c.R - c.G);
            Assert.InRange(hue, 350, 360);
            Assert.True((c.R - c.G) / (double)c.R > 0.6, c + " is not saturated enough to read as red");
        }
        Assert.True(Contrast(red, Color(resources, "Bg")) >= 4.5);
        Assert.True(Contrast(red, Color(resources, "Hover")) >= 3);
        Assert.True(Contrast(red, Composite(Color(resources, "WarningSoft"), Color(resources, "Bg"))) >= 3);
        Assert.True(Contrast(Palette.Light.Color, fill) >= 4.5);
        Assert.True(Contrast(fill, Color(resources, "Bg")) >= 3);
        Assert.Equal(red.B, Color(resources, "WarningSoft").B);
    });

    static Color Composite(Color top, Color bottom)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round((a * top.A + b * (255 - top.A)) / 255.0);
        return System.Windows.Media.Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    static Color Color(ResourceDictionary resources, string key) => ((SolidColorBrush)resources[key]).Color;
}
