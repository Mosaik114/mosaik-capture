namespace MosaikCapture.Engine.Tests.Screenshot;

// The highlighter snaps onto the words of the text line it crosses (ScreenshotEditor.SnapToText).
public sealed class HighlighterSnapTests
{
    static readonly string[] Sentence = { "Die", "zweite", "Zeile", "ist", "wichtig" };

    /// <summary>Renders one line of words; returns the ink bounds of every word.</summary>
    internal static BitmapSource TextImage(string[] words, Point start, double size, Color ink, Color background, out List<Rect> bounds)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        bounds = new List<Rect>();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 800, 200));
            double x = start.X;
            foreach (string word in words)
            {
                var text = new FormattedText(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(ink), 1.0);
                dc.DrawText(text, new Point(x, start.Y));
                bounds.Add(text.BuildGeometry(new Point(x, start.Y)).Bounds);
                x += text.WidthIncludingTrailingWhitespace + size * 0.3;
            }
        }
        var bitmap = new RenderTargetBitmap(800, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static void CoversExactly(Rect actual, Rect expected, string what)
    {
        Assert.False(actual.IsEmpty, what + ": no text found");
        Assert.InRange(actual.Left, expected.Left - 2, expected.Left + 2);
        Assert.InRange(actual.Right, expected.Right - 2, expected.Right + 2);
        Assert.True(actual.Top >= expected.Top - 2 && actual.Bottom <= expected.Bottom + 2 && actual.Height >= expected.Height * 0.6,
            what + ": height " + actual.Top + ".." + actual.Bottom + " does not match the line " + expected.Top + ".." + expected.Bottom);
    }

    [Fact]
    public void SlantedShortStrokeCoversTheWholeWordsItTouches() => WpfThread.Run(() =>
    {
        var image = TextImage(Sentence, new Point(40, 80), 20, Color.FromRgb(0x22, 0x22, 0x22), Colors.White, out var words);
        Rect line = Rect.Union(words[1], words[2]);
        double middle = (line.Top + line.Bottom) / 2;

        // starts in the middle of "zweite", ends in the middle of "Zeile", 3 px too high
        Rect r = ScreenshotEditor.SnapToText(image, (words[1].Left + words[1].Right) / 2, (words[2].Left + words[2].Right) / 2, middle - 3, 12);

        CoversExactly(r, line, "zweite Zeile");
        Assert.True(r.Left > words[0].Right && r.Right < words[3].Left, "neighbouring words must not be highlighted");
    });

    [Fact]
    public void ShortStrokeInsideOneWordHighlightsThatWord() => WpfThread.Run(() =>
    {
        var image = TextImage(Sentence, new Point(40, 80), 20, Color.FromRgb(0x22, 0x22, 0x22), Colors.White, out var words);
        double middle = (words[4].Top + words[4].Bottom) / 2;

        Rect r = ScreenshotEditor.SnapToText(image, words[4].Left + 6, words[4].Left + 26, middle, 12);

        CoversExactly(r, words[4], "wichtig");
    });

    [Fact]
    public void WorksWithLightTextOnDarkBackground() => WpfThread.Run(() =>
    {
        var image = TextImage(Sentence, new Point(60, 90), 16, Color.FromRgb(0xEE, 0xEE, 0xEE), Color.FromRgb(0x1F, 0x1F, 0x1E), out var words);
        Rect line = Rect.Union(words[2], words[3]);

        Rect r = ScreenshotEditor.SnapToText(image, words[2].Left + 2, words[3].Right - 2, (line.Top + line.Bottom) / 2, 10);

        CoversExactly(r, line, "Zeile ist");
    });

    [Fact]
    public void EmptyAreaOrPhotoStaysAPlainStroke() => WpfThread.Run(() =>
    {
        var image = TextImage(Sentence, new Point(40, 80), 20, Color.FromRgb(0x22, 0x22, 0x22), Colors.White, out _);
        Assert.True(ScreenshotEditor.SnapToText(image, 100, 300, 170, 12).IsEmpty, "empty area below the text");

        // photo-like noise: no calm background
        var random = new Random(7);
        var pixels = new byte[400 * 120 * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var photo = BitmapSource.Create(400, 120, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
        Assert.True(ScreenshotEditor.SnapToText(photo, 100, 300, 60, 12).IsEmpty, "photo");
    });

    [Fact]
    public void StrokeAtTheImageBorderIsIgnored() => WpfThread.Run(() =>
    {
        var tiny = BitmapSource.Create(3, 3, 96, 96, PixelFormats.Bgra32, null, new byte[3 * 3 * 4], 12);

        Assert.True(ScreenshotEditor.SnapToText(tiny, 0, 3, 1, 12).IsEmpty);
    });
}
