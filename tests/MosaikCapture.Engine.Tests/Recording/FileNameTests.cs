namespace MosaikCapture.Engine.Tests.Recording;

// Output files never overwrite existing ones and carry readable timestamps.
public sealed class FileNameTests : IDisposable
{
    static readonly DateTime When = new(2026, 9, 26, 14, 3, 12);
    readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void UniqueNumbersExistingFiles()
    {
        string path = directory.File("Aufnahme gekürzt.mp4");
        Assert.Equal(path, UniqueFile.Next(path));

        File.WriteAllText(path, "");
        // compare names only: a short temp path (RUNNER~1) may come back expanded
        Assert.Equal("Aufnahme gekürzt (2).mp4", Path.GetFileName(UniqueFile.Next(path)));

        File.WriteAllText(directory.File("Aufnahme gekürzt (2).mp4"), "");
        Assert.Equal("Aufnahme gekürzt (3).mp4", Path.GetFileName(UniqueFile.Next(path)));
    }

    [Fact]
    public void UniqueKeepsFilesWithoutExtension()
    {
        string path = directory.File("liste");
        File.WriteAllText(path, "");

        Assert.Equal("liste (2)", Path.GetFileName(UniqueFile.Next(path)));
    }

    [Fact]
    public void RecordingFileNameContainsTheStartTime() =>
        Assert.Equal("Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4", RecordingBackend.FileName(When));

    [Fact]
    public void ScreenshotPathIsTimestampedAndNumbered()
    {
        string first = Screenshots.NewPath(directory.Path, When);
        Assert.Equal(directory.File("Mosaiks-Screenshot 2026-09-26 14-03-12.png"), first);

        File.WriteAllText(first, "");
        Assert.Equal("Mosaiks-Screenshot 2026-09-26 14-03-12 (2).png", Path.GetFileName(Screenshots.NewPath(directory.Path, When)));

        File.WriteAllText(directory.File("Mosaiks-Screenshot 2026-09-26 14-03-12 (2).png"), "");
        Assert.Equal("Mosaiks-Screenshot 2026-09-26 14-03-12 (3).png", Path.GetFileName(Screenshots.NewPath(directory.Path, When)));
    }

    [Fact]
    public void ExportTargetsLieNextToTheSource()
    {
        string source = directory.File("Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4");

        Assert.Equal(directory.File("Mosaiks-Aufnahme 2026-09-26 14-03-12 gekürzt.mp4"), VideoExport.ExportTarget(source, gif: false));
        Assert.Equal(directory.File("Mosaiks-Aufnahme 2026-09-26 14-03-12.gif"), VideoExport.ExportTarget(source, gif: true));
    }

    [Fact]
    public void QuickExportsReserveDifferentNames()
    {
        string source = directory.File("clip.mp4");

        string first = VideoExport.ReserveTarget(source, gif: false);
        string second = VideoExport.ReserveTarget(source, gif: false);
        string gif = VideoExport.ReserveTarget(source, gif: true);

        Assert.Equal("clip gekürzt.mp4", Path.GetFileName(first));
        Assert.Equal("clip gekürzt (2).mp4", Path.GetFileName(second));
        Assert.Equal("clip.gif", Path.GetFileName(gif));
        Assert.True(File.Exists(first) && File.Exists(second) && File.Exists(gif), "the names are taken at once");
    }

    [Fact]
    public void ExportNeverOverwritesAnEarlierExport()
    {
        string source = directory.File("probe.mp4");
        File.WriteAllText(directory.File("probe gekürzt.mp4"), "");
        File.WriteAllText(directory.File("probe.gif"), "");

        Assert.Equal("probe gekürzt (2).mp4", Path.GetFileName(VideoExport.ExportTarget(source, gif: false)));
        Assert.Equal("probe (2).gif", Path.GetFileName(VideoExport.ExportTarget(source, gif: true)));
    }

    [Fact]
    public void CutInputUsesInvariantNumbersAndKeepsThePathWhole()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(new[] { "-ss", "1.25", "-to", "4.5", "-i", "C:\\Videos\\mit Leerzeichen.mp4" }, VideoExport.CutInput("C:\\Videos\\mit Leerzeichen.mp4", 1.25, 4.5));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
