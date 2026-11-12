
namespace MosaikCapture.Engine.Tests.Screenshot;

// Saving screenshots (PNG/JPEG) and Windows text recognition.
public sealed class ImageAndTextTests
{
    static BitmapSource TwoByTwo() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);

    [Fact]
    public void PngRoundTripKeepsSizeAndColors() => WpfThread.Run(() =>
    {
        byte[] png = Screenshots.Encode(TwoByTwo(), false);

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
        byte[] jpeg = Screenshots.Encode(new FormatConvertedBitmap(TwoByTwo(), PixelFormats.Bgr24, null, 0), true);

        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg.Take(2));
        var decoded = BitmapFrame.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoded.PixelWidth);
    });

    [Theory]
    [InlineData("eine Zeile", "1 Zeile · 10 Zeichen")]
    [InlineData("zwei\r\nZeilen", "2 Zeilen · 10 Zeichen")]
    [InlineData("a\nb\nc", "3 Zeilen · 3 Zeichen")]
    public void TextSummaryCountsLinesAndCharacters(string text, string expected) => Assert.Equal(expected, TextRecognition.Summary(text));

    [Fact]
    public async Task RecognizesGermanTextFromAScreenshot()
    {
        var image = WpfThread.Run(() => HighlighterSnapTests.TextImage(new[] { "Die", "zweite", "Zeile", "ist", "wichtig", "für", "uns" },
            new Point(40, 80), 20, Color.FromRgb(0x22, 0x22, 0x22), Colors.White, out _));

        var result = await Task.Run(() => TextRecognition.ReadAsync(image));

        Assert.SkipWhen(result.Status == TextRecognitionStatus.Unavailable, "no Windows OCR language is installed for this account");
        Assert.Equal(TextRecognitionStatus.Recognized, result.Status);
        Assert.Contains("zweite Zeile ist wichtig", result.Text);
    }

    [Fact]
    public async Task EmptyImageIsNoTextRatherThanAFailure()
    {
        var image = WpfThread.Run(() => HighlighterSnapTests.TextImage(Array.Empty<string>(), new Point(0, 0), 20, Colors.Black, Colors.White, out _));

        var result = await Task.Run(() => TextRecognition.ReadAsync(image));

        Assert.SkipWhen(result.Status == TextRecognitionStatus.Unavailable, "no Windows OCR language is installed for this account");
        Assert.Equal(TextRecognitionStatus.NoText, result.Status);
        Assert.Equal("Kein Text gefunden", result.FailureMessage);
    }

    [Theory]
    [InlineData("NoText", "Kein Text gefunden")]
    [InlineData("Unavailable", "Texterkennung nicht möglich")]
    [InlineData("Failed", "Texterkennung nicht möglich")]
    public void FailureMessageTellsNoTextFromAFailure(string status, string expected) =>
        Assert.Equal(expected, new TextRecognitionResult((TextRecognitionStatus)Enum.Parse(typeof(TextRecognitionStatus), status)).FailureMessage);
}
