namespace MosaikCapture.Engine.Tests.Core;

public sealed class PaletteTests
{
    static readonly string[] Keys = { "Bg", "Sel", "Hover", "Line", "Text", "Muted", "Accent", "Chip", "AccentSoft" };

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
    public void LightAndDarkShareTheAccentButNotTheBackground() => WpfThread.Run(() =>
    {
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        Palette.Apply(light, true);
        Palette.Apply(dark, false);

        Assert.Equal(Color(light, "Accent"), Color(dark, "Accent"));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x57), Color(light, "Accent"));
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

    static Color Color(ResourceDictionary resources, string key) => ((SolidColorBrush)resources[key]).Color;
}
