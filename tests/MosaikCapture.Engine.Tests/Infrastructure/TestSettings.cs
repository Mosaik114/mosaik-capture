namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>A temporary settings file for the lifetime of the object; tests read it with <see cref="Load"/>.</summary>
internal sealed class TestSettings : IDisposable
{
    readonly TempDirectory directory = new();

    /// <param name="ini">Content of the INI file.</param>
    /// <param name="encoding">Defaults to UTF-16 ("Unicode"), as written by the host.</param>
    public TestSettings(string ini, Encoding? encoding = null)
    {
        FileName = directory.File("settings.ini");
        File.WriteAllText(FileName, ini, encoding ?? Encoding.Unicode);
    }

    public string FileName { get; }

    public CaptureSettingsSnapshot Load() => CaptureSettings.Load(FileName);

    public void Dispose() => directory.Dispose();
}
