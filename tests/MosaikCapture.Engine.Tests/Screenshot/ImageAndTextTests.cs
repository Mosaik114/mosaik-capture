using System.Threading.Tasks;

namespace MosaikCapture.Engine.Tests.Screenshot;

// Saving screenshots (PNG/JPEG) and Windows text recognition.
public sealed class ImageAndTextTests
{
    static BitmapSource TwoByTwo() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);

    [Fact]
    public void PngRoundTripKeepsSizeAndColors() => WpfThread.Run(() =>
    {
        byte[] png = Shot.Encode(TwoByTwo(), false);

        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png.Take(4));
        var decoded = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoded.PixelWidth);
        Assert.Equal(2, decoded.PixelHeight);
        var actual = new byte[16];
        new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 8, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, actual);
    });

    [Fact]
    public void JpegIsWrittenAsJpeg() => WpfThread.Run(() =>
    {
        byte[] jpeg = Shot.Encode(new FormatConvertedBitmap(TwoByTwo(), PixelFormats.Bgr24, null, 0), true);

        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg.Take(2));
        var decoded = BitmapFrame.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoded.PixelWidth);
    });

    [Theory]
    [InlineData("eine Zeile", "1 Zeile · 10 Zeichen")]
    [InlineData("zwei\r\nZeilen", "2 Zeilen · 10 Zeichen")]
    [InlineData("a\nb\nc", "3 Zeilen · 3 Zeichen")]
    public void TextSummaryCountsLinesAndCharacters(string text, string expected) => Assert.Equal(expected, ShotText.Summary(text));

    [Fact]
    public async Task RecognizesGermanTextFromAScreenshot()
    {
        var image = WpfThread.Run(() => HighlighterSnapTests.TextImage(new[] { "Die", "zweite", "Zeile", "ist", "wichtig", "für", "uns" },
            new Point(40, 80), 20, Color.FromRgb(0x22, 0x22, 0x22), Colors.White, out _));

        string? text = await Task.Run(() => ShotText.Read(image));

        Assert.SkipWhen(text == null, "no Windows OCR language is installed for this account");
        Assert.Contains("zweite Zeile ist wichtig", text);
    }
}
