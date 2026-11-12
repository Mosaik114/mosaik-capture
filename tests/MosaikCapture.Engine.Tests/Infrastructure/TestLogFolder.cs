[assembly: AssemblyFixture(typeof(TestLogFolder))]

namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Points the engine's error log at a temporary folder for the whole test run, so tests neither write
/// into the user's real log in %LOCALAPPDATA% nor depend on what is in it.
/// </summary>
public sealed class TestLogFolder : IDisposable
{
    readonly TempDirectory folder = new();

    public TestLogFolder()
    {
        Default = ErrorLog.Directory;
        ErrorLog.Directory = folder.Path;
    }

    /// <summary>Where the engine logs outside of tests.</summary>
    internal static string Default { get; private set; } = "";

    public void Dispose()
    {
        ErrorLog.Directory = Default;
        folder.Dispose();
    }
}
