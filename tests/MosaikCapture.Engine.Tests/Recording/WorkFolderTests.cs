namespace MosaikCapture.Engine.Tests.Recording;

// Where recordings are assembled and how leftovers of crashed recordings are cleaned up.
public sealed class WorkFolderTests
{
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void WorkFoldersLiveBelowLocalAppData()
    {
        Assert.Equal(Path.Combine(LocalAppData, "MosaikCapture"), CaptureSettings.Home);
        Assert.Equal(Path.Combine(LocalAppData, "MosaikCapture", "recording"), CaptureSettings.RecordingWorkRoot);
        Assert.Equal(Path.Combine(LocalAppData, "Mosaiks-Aufnahme", "Aufnahme"), CaptureSettings.LegacyRecordingWorkRoot);
    }

    [Fact]
    public void OnlyFoldersOlderThanAnHourAreRemoved()
    {
        using var root = new TempDirectory();
        string old = Directory.CreateDirectory(root.File("20260101-120000-000")).FullName;
        File.WriteAllText(Path.Combine(old, "segment-01.mkv"), "x");
        Directory.SetCreationTime(old, DateTime.Now.AddHours(-2));
        string recent = Directory.CreateDirectory(root.File("20260101-130000-000")).FullName;

        RecordingBackend.RemoveOldWorkFolders(root.Path);

        Assert.False(Directory.Exists(old), "leftover of a crashed recording is still there");
        Assert.True(Directory.Exists(recent), "a recording that may still run was removed");
    }

    [Fact]
    public void RawDataKeptAfterAFailedSaveIsNeverRemoved()
    {
        using var root = new TempDirectory();
        string kept = Directory.CreateDirectory(root.File("20260101-120000-000")).FullName;
        File.WriteAllText(Path.Combine(kept, "segment-01.mkv"), "x");
        File.WriteAllText(Path.Combine(kept, RecordingSession.KeptMarkerName), "C:\\Videos\\out.mp4");
        Directory.SetCreationTime(kept, DateTime.Now.AddDays(-30));

        RecordingBackend.RemoveOldWorkFolders(root.Path);

        Assert.True(File.Exists(Path.Combine(kept, "segment-01.mkv")), "a rescuable recording was deleted");
    }

    [Fact]
    public void MissingRootIsNoError()
    {
        using var root = new TempDirectory();

        RecordingBackend.RemoveOldWorkFolders(root.File("missing"));
    }
}
