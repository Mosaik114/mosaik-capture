namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Screen recording, as used by the host bridge and the recording windows: pick an area like for a
/// screenshot, count down 3-2-1, then ffmpeg records via the GPU (Desktop Duplication, NVENC) while
/// system sound and microphone are captured via WASAPI and laid under the video when saving.
/// While recording, a frame around the area and a toolbar are shown (both invisible in the video);
/// mouse clicks show up as circles. Afterwards a card offers trimming, GIF, folder and delete.
/// This is a thin static facade over one <see cref="RecordingController"/> with the real windows.
/// </summary>
internal static class ScreenRecorder
{
    static RecordingController? controller;
    static WpfRecordingUi? windows;

    /// <summary>Called once by <see cref="EngineComposition"/>, which builds the object graph.</summary>
    internal static void Use(RecordingController recordingController, WpfRecordingUi recordingWindows)
    {
        controller = recordingController;
        windows = recordingWindows;
    }

    static RecordingController Controller => controller ?? throw EngineComposition.NotComposed();

    static WpfRecordingUi ui => windows ?? throw EngineComposition.NotComposed();

    public static bool Busy => Controller.Busy;
    public static bool Live => Controller.Live;
    public static bool Paused => Controller.Paused;
    public static double Elapsed => Controller.Elapsed;
    public static float MicLevel => Controller.MicrophoneLevel;

    // Audio choices as shown in the toolbar (the setters do not persist; see SetMic and friends).
    public static bool Mic { get => Controller.Microphone; set => Controller.Microphone = value; }
    public static bool Sound { get => Controller.SystemSound; set => Controller.SystemSound = value; }
    public static bool Clicks { get => Controller.ShowClicks; set => Controller.ShowClicks = value; }

    public static void Toggle() => Controller.Toggle();

    /// <summary>Toggles after a short delay so that whatever triggered it has left the screen.</summary>
    public static void ToggleSoon()
    {
        var timer = new DispatcherTimer { Interval = Motion.TriggerDelay };
        timer.Tick += delegate { timer.Stop(); Toggle(); };
        timer.Start();
    }

    public static void Stop() => Controller.StopAsync().LogFailures("Saving a recording");
    public static void Discard() => Controller.Discard();
    public static void TogglePause() => Controller.TogglePause();
    public static void SetMic(bool on) => Controller.SetMicrophone(on);
    public static void SetSound(bool on) => Controller.SetSystemSound(on);
    public static void SetClicks(bool on) => Controller.SetShowClicks(on);

    // ---------- After recording (actions of the card and the trim window) ----------

    public static void Trim(RecordedVideo video, RECT work)
    {
        ui.Card.HideNow();
        ui.TrimWindow.Open(video, work);
    }

    public static void MakeGif(RecordedVideo video, RECT work)
    {
        ui.Card.Busy(UiText.Recording.CreatingGif);
        VideoExport.Export(video, 0, video.Duration, false, true, ui.Card.Progress, (result, error) =>
        {
            if (result == null) ui.Card.Problem(UiText.Recording.GifFailed, error ?? UiText.Common.UnknownError, work);
            else Exported(result, gif: true, work);
        });
    }

    /// <summary>A trimmed video or GIF is done: copy it and show it on the card.</summary>
    public static void Exported(RecordedVideo video, bool gif, RECT work) =>
        ExportedAsync(video, gif, work).OnFailure("Showing an export", () => ui.Card.Problem(UiText.Recording.Failed, UiText.Common.UnknownError, work));

    static async Task ExportedAsync(RecordedVideo video, bool gif, RECT work)
    {
        bool copied = await ui.CopyFileToClipboardAsync(video.Path);
        string heading = gif
            ? copied ? UiText.Recording.GifSaved : UiText.Recording.GifSavedNotCopied
            : copied ? UiText.Recording.TrimmedAndCopied : UiText.Recording.TrimmedNotCopied;
        ui.Card.Present(video, heading, work);
    }

    public static void TrimWindowClosed(RecordedVideo video, RECT work)
    {
        if (File.Exists(video.Path)) ui.Card.Present(video, UiText.Recording.Saved, work);
    }
}
