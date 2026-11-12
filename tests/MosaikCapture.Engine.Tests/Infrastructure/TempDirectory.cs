namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>A fresh directory below %TEMP% that is deleted again when the test is done.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MosaikCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }                    // a file is still in use (e.g. a video): leave it to %TEMP% cleanup
        catch (UnauthorizedAccessException) { }
    }
}
