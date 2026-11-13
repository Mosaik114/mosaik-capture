namespace MosaikCapture.Engine.Tests.Core;

// The names and message numbers themselves are checked against contract/contract.json in ContractTests.
public sealed class CaptureBridgeTests
{
    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "--screenshot" }, 1)]
    [InlineData(new[] { "--recording" }, 2)]
    [InlineData(new[] { "--idle" }, 0)]
    [InlineData(new[] { "--unknown" }, 0)]      // logged and started idle, never an unasked screenshot
    [InlineData(new[] { "-screenshot" }, 0)]
    [InlineData(new[] { "--recording", "--idle" }, 2)]
    public void CommandLineSelectsTheCommand(string[] args, int expected) => Assert.Equal(expected, CaptureBridge.ParseCommand(args));

    [Fact]
    public void ForwardedCommandsMapToTheWindowMessages()
    {
        Assert.Equal(CaptureBridge.ScreenshotMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { "--screenshot" }));
        Assert.Equal(CaptureBridge.RecordingMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { "--recording" }));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 3)]
    public void StateReplyEncodesBusyAndLive(bool busy, bool live, int expected) => Assert.Equal(expected, CaptureBridge.StateFlags(busy, live));

    [Fact]
    public void RecordingIsIdleInitially()
    {
        Assert.False(ScreenRecorder.Busy);
        Assert.False(ScreenRecorder.Live);
        Assert.False(ScreenRecorder.Paused);
        Assert.Equal(0, ScreenRecorder.Elapsed);
    }
}
