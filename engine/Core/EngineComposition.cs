namespace MosaikCapture.Engine.Core;

/// <summary>
/// Builds the engine's object graph once, at startup: the two flows with their real windows and system
/// services, handed to the static facades (<see cref="ScreenRecorder"/>, <see cref="Screenshots"/>) that the
/// host bridge and the windows call. Everything below the facades receives its collaborators through
/// constructors, which is what the tests use instead.
/// </summary>
internal static class EngineComposition
{
    static readonly object gate = new();
    static bool composed;

    /// <summary>Creates the controllers once (no window is created here; each appears on first use).</summary>
    public static void Compose()
    {
        lock (gate)
        {
            if (composed) return;
            var recordingWindows = new WpfRecordingUi();
            ScreenRecorder.Use(new RecordingController(recordingWindows, new RecordingBackend()), recordingWindows);
            Screenshots.Use(new ScreenshotController(new WpfScreenshotUi(), new ScreenshotStore()));
            composed = true;
        }
    }

    internal static InvalidOperationException NotComposed() =>
        new("The engine's object graph is not built yet: call EngineComposition.Compose() at startup.");
}
