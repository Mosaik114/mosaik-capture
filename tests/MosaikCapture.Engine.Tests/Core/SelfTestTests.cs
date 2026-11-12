namespace MosaikCapture.Engine.Tests.Core;

// `MosaikCapture.Engine.exe --self-test <report>` is used by the build script; keep it working.
public sealed class SelfTestTests
{
    [Fact]
    public void SelfTestPassesAndWritesAJsonReport()
    {
        using var directory = new TempDirectory();
        string report = directory.File("capture-test.json");
        string? previous = Environment.GetEnvironmentVariable(CaptureSettings.TestConfigVariable);
        int exitCode = Environment.ExitCode;
        try
        {
            WpfThread.Run(() => Diagnostics.SelfTest.Run(report));

            Assert.Matches("^\\{\"passed\":[1-9][0-9]*,\"failed\":0\\}$", File.ReadAllText(report, Encoding.UTF8).TrimStart('﻿'));
            Assert.Equal(exitCode, Environment.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureSettings.TestConfigVariable, previous);
            Environment.ExitCode = exitCode;
        }
    }
}
