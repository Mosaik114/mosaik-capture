namespace MosaikCapture.Engine.Tests.Recording;

// Formatting of times, sizes and ffmpeg numbers, and parsing of ffprobe/ffmpeg output.
public sealed class RecUiTests
{
    [Theory]
    [InlineData(7.9, "0:07")]
    [InlineData(59.99, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3727, "1:02:07")]
    [InlineData(-3, "0:00")]
    public void ClockShowsMinutesAndHours(double seconds, string expected) => Assert.Equal(expected, RecUi.Clock(seconds));

    [Theory]
    [InlineData(7.44, "0:07,4")]
    [InlineData(59.96, "1:00,0")]   // tenths round over the full minute
    [InlineData(0.06, "0:00,1")]
    [InlineData(-1, "0:00,0")]
    public void FineShowsTenths(double seconds, string expected) => Assert.Equal(expected, RecUi.Fine(seconds));

    [Theory]
    [InlineData(0, "1 KB")]
    [InlineData(300, "1 KB")]
    [InlineData(512 * 1024, "512 KB")]
    [InlineData(1024 * 1024 - 1, "1023 KB")]
    [InlineData(1572864, "1,5 MB")]
    [InlineData(99.94 * 1048576, "99,9 MB")]
    [InlineData(150L * 1048576, "150 MB")]
    public void BytesUseKilobytesUpToOneMegabyteAndAGermanComma(double bytes, string expected) => Assert.Equal(expected, RecUi.Bytes((long)bytes));

    [Fact]
    public void FfmpegNumbersAlwaysUseADotEvenOnGermanWindows()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("2.5", RecUi.Num(2.5));
            Assert.Equal("0.333333", RecUi.Num(1.0 / 3));
            Assert.Equal("12", RecUi.Num(12));
            Assert.Equal("0", RecUi.Num(0.0000001));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void FieldReadsExactlyTheRequestedFfprobeKey()
    {
        const string probe = "duration_ts=315000\r\nduration=3.500000\r\nwidth=1280\r\nheight=720\r\nwidth=640\r\nstart_time=N/A\r\n";

        Assert.Equal(3.5, RecUi.Field(probe, "duration"));   // duration_ts must not match
        Assert.Equal(1280.0, RecUi.Field(probe, "width"));   // first occurrence
        Assert.Equal(720.0, RecUi.Field(probe, "height"));
        Assert.Equal(0.0, RecUi.Field(probe, "start_time")); // N/A
        Assert.Equal(0.0, RecUi.Field(probe, "bit_rate"));   // missing
    }

    [Fact]
    public void FieldIgnoresSurroundingWhitespaceAndParsesExponents()
    {
        Assert.Equal(-0.023, RecUi.Field("  start_time=-0.023000  \n", "start_time"), 6);
        Assert.Equal(1500.0, RecUi.Field("duration=1.5e3\n", "duration"));
    }

    [Theory]
    [InlineData("[in] Invalid data\r\nConversion failed!\r\n  \r\n", "Conversion failed!")]
    [InlineData("only line", "only line")]
    [InlineData(null, "Unbekannter Fehler")]
    [InlineData("\r\n \n", "Unbekannter Fehler")]
    public void LastLineIsTheLastNonEmptyFfmpegLine(string? text, string expected) => Assert.Equal(expected, RecUi.LastLine(text!));

    [Fact]
    public void LoadImageDoesNotLockTheFile() => WpfThread.Run(() =>
    {
        using var directory = new TempDirectory();
        string file = directory.File("bild.png");
        File.WriteAllBytes(file, Shot.Encode(BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, new byte[3 * 2 * 4], 12), false));

        var image = RecUi.LoadImage(file);
        File.Delete(file);   // throws if the image still holds the file open

        Assert.Equal(3, image.PixelWidth);
        Assert.True(image.IsFrozen);
    });
}
