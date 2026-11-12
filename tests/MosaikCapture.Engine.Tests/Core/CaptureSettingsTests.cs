namespace MosaikCapture.Engine.Tests.Core;

public sealed class CaptureSettingsTests
{
    const string Full =
        "[capture]\r\nscreenshot_folder=C:\\Bilder\\Grüße\r\nrecording_folder=C:\\Videos\\Aufnahmen\r\n" +
        "microphone=1\r\nsystem_audio=0\r\nshow_clicks=0\r\nfps=30\r\ncountdown=5\r\n";

    [Fact]
    public void ReadsAllValuesFromUnicodeIni()
    {
        using var settings = new TestSettings(Full);
        var loaded = settings.Load();

        Assert.Equal("C:\\Bilder\\Grüße", loaded.ScreenshotFolder);
        Assert.Equal("C:\\Videos\\Aufnahmen", loaded.RecordingFolder);
        Assert.True(loaded.Microphone);
        Assert.False(loaded.SystemAudio);
        Assert.False(loaded.Clicks);
        Assert.Equal(30, loaded.Fps);
        Assert.Equal(5, loaded.Countdown);
    }

    [Fact]
    public void MissingKeysFallBackToDefaults()
    {
        using var settings = new TestSettings("[capture]\r\n");
        var loaded = settings.Load();

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mosaiks-Screenshots"), loaded.ScreenshotFolder);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mosaiks-Aufnahmen"), loaded.RecordingFolder);
        Assert.False(loaded.Microphone);
        Assert.True(loaded.SystemAudio);
        Assert.True(loaded.Clicks);
        Assert.Equal(60, loaded.Fps);
        Assert.Equal(3, loaded.Countdown);
    }

    [Fact]
    public void MissingFileFallsBackToDefaults()
    {
        using var settings = new TestSettings("");
        File.Delete(settings.FileName);
        var loaded = settings.Load();

        Assert.False(loaded.Microphone);
        Assert.True(loaded.SystemAudio);
        Assert.Equal(60, loaded.Fps);
        Assert.Equal(3, loaded.Countdown);
    }

    [Fact]
    public void KeysOutsideTheCaptureSectionAreIgnored()
    {
        using var settings = new TestSettings("[other]\r\nfps=30\r\nmicrophone=1\r\n[capture]\r\ncountdown=0\r\n");
        var loaded = settings.Load();

        Assert.Equal(60, loaded.Fps);
        Assert.False(loaded.Microphone);
        Assert.Equal(0, loaded.Countdown);
    }

    [Theory]
    [InlineData("fps=45", 60, 3)]
    [InlineData("fps=abc", 60, 3)]
    [InlineData("fps=", 60, 3)]
    [InlineData("countdown=7", 60, 3)]
    [InlineData("countdown=-1", 60, 3)]
    [InlineData("countdown=zwei", 60, 3)]
    [InlineData("countdown=0", 60, 0)]
    [InlineData("fps=30\r\ncountdown=5", 30, 5)]
    public void InvalidNumbersAreNormalized(string lines, int fps, int countdown)
    {
        using var settings = new TestSettings("[capture]\r\n" + lines + "\r\n");
        var loaded = settings.Load();

        Assert.Equal(fps, loaded.Fps);
        Assert.Equal(countdown, loaded.Countdown);
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("-1", true)]
    [InlineData("0", false)]
    [InlineData("ja", false)]   // not a number: default (off)
    public void MicrophoneIsOnForAnyNonZeroNumber(string value, bool expected)
    {
        using var settings = new TestSettings("[capture]\r\nmicrophone=" + value + "\r\n");
        var loaded = settings.Load();

        Assert.Equal(expected, loaded.Microphone);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(0, 60)]
    [InlineData(120, 60)]
    [InlineData(-30, 60)]
    public void NormalizeFpsOnlyAllows30And60(int value, int expected) => Assert.Equal(expected, CaptureSettings.NormalizeFps(value));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(1, 3)]
    [InlineData(4, 3)]
    [InlineData(10, 3)]
    public void NormalizeCountdownOnlyAllows0And3And5(int value, int expected) => Assert.Equal(expected, CaptureSettings.NormalizeCountdown(value));

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 2)]
    [InlineData(false, false, true, 4)]
    [InlineData(true, true, true, 7)]
    [InlineData(true, false, true, 5)]
    public void AudioFlagsMatchTheHostProtocol(bool mic, bool sound, bool clicks, int expected) =>
        Assert.Equal(expected, CaptureSettings.AudioFlags(mic, sound, clicks));

    [Fact]
    public void TheOverrideVariableNamesTheFile() =>
        Assert.Equal("D:\\qa\\settings.ini", CaptureSettings.ResolveFileName("D:\\qa\\settings.ini"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutOverrideTheFileIsInLocalAppData(string? value)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaikCapture", "settings.ini");

        Assert.Equal(expected, CaptureSettings.ResolveFileName(value));
    }

    [Fact]
    public void ASnapshotDoesNotChangeWhenTheFileDoes()
    {
        using var settings = new TestSettings("[capture]\r\nfps=30\r\n");
        var loaded = settings.Load();

        File.WriteAllText(settings.FileName, "[capture]\r\nfps=60\r\n", Encoding.Unicode);

        Assert.Equal(30, loaded.Fps);
        Assert.Equal(60, settings.Load().Fps);
    }

    [Fact]
    public void SnapshotHandsOverTheAudioChoices()
    {
        using var settings = new TestSettings("[capture]\r\nmicrophone=1\r\nsystem_audio=0\r\nshow_clicks=1\r\n");

        Assert.Equal(new AudioSettings(true, false, true), settings.Load().Audio);
    }
}
