namespace MosaikCapture.Engine.Recording;

/// <summary>Audio choices of the user; kept between recordings and persisted by the host.</summary>
internal readonly struct AudioSettings
{
    public AudioSettings(bool microphone, bool systemSound, bool showClicks)
    {
        Microphone = microphone;
        SystemSound = systemSound;
        ShowClicks = showClicks;
    }

    public bool Microphone { get; }
    public bool SystemSound { get; }
    public bool ShowClicks { get; }
}

/// <summary>One running recording: video segments between pauses plus the audio tracks.</summary>
internal interface IRecordingSession
{
    Int32Rect Area { get; }
    DateTime Begun { get; }
    /// <summary>Why the recording failed (shown to the user).</summary>
    string Reason { get; }
    bool Paused { get; }
    double Elapsed { get; }
    float MicrophoneLevel { get; }
    /// <summary>True if the sound could not be recorded although the video was.</summary>
    bool AudioLost { get; }
    /// <summary>After a failed save: where the raw segments and sound tracks were kept (null if nothing was).</summary>
    string? RawDataFolder { get; }

    /// <summary>ffmpeg stopped on its own (raised on a background thread).</summary>
    event Action? Broke;

    /// <summary>Starts a new video segment (also the first one); returns once ffmpeg was started.</summary>
    Task ResumeAsync();
    void Pause();
    void SetMicrophone(bool on);
    void SetSystemSound(bool on);
    /// <summary>
    /// Assembles the segments into <paramref name="path"/> without blocking a thread; null on failure (see
    /// <see cref="Reason"/>). Cancelling kills a running ffmpeg.
    /// </summary>
    Task<RecordedVideo?> FinishAsync(string path, CancellationToken cancellation);
    /// <summary>Blocking: stops everything and deletes the work files.</summary>
    void Discard();
}

/// <summary>Everything the recording flow needs from the system (ffmpeg, monitors, files, settings).</summary>
internal interface IRecordingBackend
{
    bool FfmpegAvailable { get; }
    /// <summary>Starts the encoder detection early so it is done when the countdown ends.</summary>
    void PrepareEncoder();
    void RemoveLeftovers();
    /// <summary>The GPU output that shows the monitor, for ddagrab.</summary>
    OutputLookup FindOutput(RECT monitor, out int output, out RECT desktop);
    /// <summary>Creates a session and starts its audio (audio already runs during the countdown).</summary>
    IRecordingSession StartSession(Int32Rect area, int output, RECT desktop, AudioSettings audio, int fps);
    /// <summary>A new file name in <paramref name="folder"/> for a recording started at <paramref name="begun"/>.</summary>
    string NewRecordingPath(DateTime begun, string folder);
    /// <summary>Reads the settings once for this recording.</summary>
    CaptureSettingsSnapshot LoadSettings();
    void SaveSettings(AudioSettings settings);
}

/// <summary>The windows of the recording flow, driven by <see cref="RecordingController"/>.</summary>
internal interface IRecordingUi
{
    RECT WorkAreaAtCursor();
    /// <summary>Lets the user pick an area; exactly one of the callbacks follows.</summary>
    void PickArea(Action<Int32Rect, RECT, RECT> picked, Action cancelled);
    void ShowFrame(Int32Rect area, RECT monitor);
    void HideFrame();
    void SetFramePaused(bool paused);
    /// <summary>
    /// Counts down; <paramref name="launch"/> runs shortly before zero, then <paramref name="finished"/>,
    /// or <paramref name="cancelled"/> on Esc.
    /// </summary>
    void StartCountdown(Int32Rect area, RECT monitor, int seconds, Action launch, Action finished, Action cancelled);
    void StopCountdown();
    void ShowToolbar(Int32Rect area, RECT work);
    void RefreshToolbar();
    void HideToolbar();
    void StartClickHighlights(Int32Rect area, bool enabled);
    void SetClickHighlights(bool enabled);
    void StopClickHighlights();
    void ShowSaving(double seconds, RECT work);
    void ShowProblem(string heading, string reason, RECT work);
    /// <summary>The recording could not be saved; <paramref name="rawDataFolder"/> (if any) is offered for rescue.</summary>
    void ShowSaveFailed(string reason, string? rawDataFolder, RECT work);
    void ShowRecording(RecordedVideo video, string heading, RECT work);
    /// <summary>Puts the file on the clipboard; false if another app held the clipboard too long.</summary>
    Task<bool> CopyFileToClipboardAsync(string path);
    /// <summary>After an unexpected error: closes every live recording window (each step on its own).</summary>
    void CloseLiveWindows();
    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);
}
