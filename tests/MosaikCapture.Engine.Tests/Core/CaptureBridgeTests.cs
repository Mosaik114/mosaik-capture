namespace MosaikCapture.Engine.Tests.Core;

// The Rust host depends on these values; changing any of them breaks the IPC contract.
public sealed class CaptureBridgeTests
{
    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "--screenshot" }, 1)]
    [InlineData(new[] { "--recording" }, 2)]
    [InlineData(new[] { "--idle" }, 0)]
    [InlineData(new[] { "--unknown" }, 1)]
    [InlineData(new[] { "--recording", "--idle" }, 2)]
    public void CommandLineSelectsTheCommand(string[] args, int expected) => Assert.Equal(expected, CaptureBridge.ParseCommand(args));

    [Fact]
    public void ForwardedCommandsMapToTheWindowMessages()
    {
        Assert.Equal(CaptureBridge.ScreenshotMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { "--screenshot" }));
        Assert.Equal(CaptureBridge.RecordingMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { "--recording" }));
    }

    [Fact]
    public void IpcContractIsUnchanged()
    {
        Assert.Equal("Local\\MosaiksCaptureEngine", CaptureBridge.MutexName);
        Assert.Equal("Mosaiks Capture Engine", CaptureBridge.Title);
        Assert.Equal(0x8000, CaptureBridge.CommandBase);
        Assert.Equal(0x8001, CaptureBridge.ScreenshotMessage);
        Assert.Equal(0x8002, CaptureBridge.RecordingMessage);
        Assert.Equal(0x8003, CaptureBridge.QuitMessage);
        Assert.Equal(0x800A, CaptureBridge.StateMessage);
        Assert.Equal("MosaiksAufnahmeRust", CaptureSettings.HostWindowClass);
        Assert.Equal(0x8014, CaptureSettings.SaveAudioMessage);
        Assert.Equal("MOSAIK_CAPTURE_TEST_CONFIG", CaptureSettings.TestConfigVariable);
        Assert.Equal("Fehler.log", ErrorLog.FileName);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 3)]
    public void StateReplyEncodesBusyAndLive(bool busy, bool live, int expected) => Assert.Equal(expected, CaptureBridge.StateFlags(busy, live));

    [Fact]
    public void RecordingIsIdleInitially()
    {
        Assert.False(Rec.Busy);
        Assert.False(Rec.Live);
        Assert.False(Rec.Paused);
        Assert.Equal(0, Rec.Elapsed);
    }
}
