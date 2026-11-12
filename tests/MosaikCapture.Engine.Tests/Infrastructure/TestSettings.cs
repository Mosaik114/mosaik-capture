namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Points the engine at a temporary settings file (via MOSAIK_CAPTURE_TEST_CONFIG) for the lifetime
/// of the object, so tests never read or touch the user's real settings.
/// </summary>
internal sealed class TestSettings : IDisposable
{
    readonly TempDirectory directory = new();
    readonly string? previous = Environment.GetEnvironmentVariable(CaptureSettings.TestConfigVariable);

    /// <param name="ini">Content of the INI file.</param>
    /// <param name="encoding">Defaults to UTF-16 ("Unicode"), as written by the host.</param>
    public TestSettings(string ini, Encoding? encoding = null)
    {
        FileName = directory.File("settings.ini");
        File.WriteAllText(FileName, ini, encoding ?? Encoding.Unicode);
        Environment.SetEnvironmentVariable(CaptureSettings.TestConfigVariable, FileName);
    }

    public string FileName { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CaptureSettings.TestConfigVariable, previous);
        directory.Dispose();
    }
}
