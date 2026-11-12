namespace MosaikCapture.Engine.Recording;

internal enum RecordingPhase
{
    Idle,
    /// <summary>The user is picking the area.</summary>
    Picking,
    /// <summary>3-2-1 before the first frame.</summary>
    Counting,
    Running,
    /// <summary>ffmpeg joins the segments.</summary>
    Saving,
}

/// <summary>
/// The recording flow as a state machine: Idle → Picking → Counting → Running → Saving → Idle.
/// Cancelling the pick or the countdown returns to Idle; ffmpeg dying on its own ("broke") saves what
/// was recorded. All methods run on the UI thread; the blocking work happens on background threads.
/// </summary>
internal sealed class RecordingController
{
    readonly IRecordingUi ui;
    readonly IRecordingBackend backend;
    IRecordingSession? session;
    RECT workArea;

    public RecordingController(IRecordingUi ui, IRecordingBackend backend)
    {
        this.ui = ui;
        this.backend = backend;
    }

    public RecordingPhase Phase { get; private set; }

    // Audio choices; loaded from the settings when a recording starts and kept in between.
    public bool Microphone { get; set; }
    public bool SystemSound { get; set; } = true;
    public bool ShowClicks { get; set; } = true;

    public bool Busy => Phase != RecordingPhase.Idle;
    public bool Live => Phase is RecordingPhase.Counting or RecordingPhase.Running;
    public bool Paused => session != null && session.Paused;
    public double Elapsed => session?.Elapsed ?? 0;
    public float MicrophoneLevel => session != null && Microphone ? session.MicrophoneLevel : 0;

    /// <summary>The host's trigger: start, cancel during the countdown, stop while recording.</summary>
    public void Toggle()
    {
        switch (Phase)
        {
            case RecordingPhase.Idle:
                Pick();
                break;
            case RecordingPhase.Counting:
                ui.StopCountdown();
                CancelCountdown();
                break;
            case RecordingPhase.Running:
                StopAsync().LogFailures("Saving a recording");
                break;
        }
    }

    void Pick()
    {
        if (!backend.FfmpegAvailable)
        {
            ui.ShowProblem(UiText.Recording.FfmpegMissing, UiText.Recording.FfmpegInstallHint, ui.WorkAreaAtCursor());
            return;
        }
        var settings = backend.LoadSettings();
        Microphone = settings.Microphone;
        SystemSound = settings.SystemSound;
        ShowClicks = settings.ShowClicks;
        backend.RemoveLeftovers();
        backend.PrepareEncoder();   // probe the encoder while the area is being picked
        Phase = RecordingPhase.Picking;
        ui.PickArea(AreaPicked, PickCancelled);
    }

    internal void PickCancelled()
    {
        if (Phase == RecordingPhase.Picking) Phase = RecordingPhase.Idle;
    }

    internal void AreaPicked(Int32Rect area, RECT monitor, RECT work)
    {
        workArea = work;
        area = RecordingArea.Fit(area, monitor);
        if (!backend.TryFindOutput(monitor, out int output, out RECT desktop))
        {
            Phase = RecordingPhase.Idle;
            ui.ShowProblem(UiText.Recording.NotPossible, UiText.Recording.MonitorUnreachable, work);
            return;
        }
        try
        {
            session = backend.StartSession(area, output, desktop, new AudioSettings(Microphone, SystemSound, ShowClicks));
            session.Broke += OnSessionBroke;
        }
        catch (Exception ex)   // e.g. the work folder cannot be created or the audio API fails
        {
            ErrorLog.Write("Starting a recording", ex);
            session = null;
            Phase = RecordingPhase.Idle;
            ui.ShowProblem(UiText.Recording.NotPossible, ex.Message, work);
            return;
        }
        Phase = RecordingPhase.Counting;
        ui.ShowFrame(area, monitor);
        ui.StartCountdown(area, monitor, () => LaunchAsync().LogFailures("Starting ffmpeg"), CountdownFinished, CancelCountdown);
    }

    /// <summary>Shortly before zero: start ffmpeg so the first frame arrives exactly when the countdown ends.</summary>
    internal Task LaunchAsync() => session?.ResumeAsync() ?? Task.CompletedTask;

    internal void CountdownFinished()
    {
        if (Phase != RecordingPhase.Counting || session == null) return;
        Phase = RecordingPhase.Running;
        ui.ShowToolbar(session.Area, workArea);
        ui.StartClickHighlights(session.Area, ShowClicks);
    }

    /// <summary>Esc during the countdown.</summary>
    internal void CancelCountdown()
    {
        if (Phase != RecordingPhase.Counting) return;
        Phase = RecordingPhase.Idle;
        ui.HideFrame();
        var cancelled = session;
        session = null;
        if (cancelled != null) Task.Run(cancelled.Discard).LogFailures("Discarding a recording");
    }

    // ffmpeg exited on its own (e.g. graphics driver, locked screen): save what we have.
    void OnSessionBroke() => ui.Post(() =>
    {
        if (Phase == RecordingPhase.Counting)
        {
            ui.StopCountdown();
            Phase = RecordingPhase.Running;
        }
        StopAsync().LogFailures("Saving a recording");
    });

    /// <summary>Stops and saves; completes when the video is saved (or failed).</summary>
    public async Task StopAsync()
    {
        if (Phase != RecordingPhase.Running) return;
        Phase = RecordingPhase.Saving;
        HideLiveWindows();
        var finished = session!;
        session = null;
        var work = workArea;
        ui.ShowSaving(finished.Elapsed, work);
        RecordedVideo? video = null;
        try
        {
            string path = backend.NewRecordingPath(finished.Begun);
            video = await Task.Run(() => finished.Finish(path));
        }
        catch (Exception ex)   // must always get back to Idle and tell the user
        {
            ErrorLog.Write("Saving a recording", ex);
        }
        Phase = RecordingPhase.Idle;
        if (video == null)
        {
            ui.ShowProblem(UiText.Recording.Failed, finished.Reason, work);
            return;
        }
        ui.CopyFileToClipboard(video.Path);
        ui.ShowRecording(video, finished.AudioLost ? UiText.Recording.SavedWithoutAudio : UiText.Recording.Saved, work);
    }

    public void Discard()
    {
        if (Phase != RecordingPhase.Running) return;
        Phase = RecordingPhase.Idle;
        HideLiveWindows();
        var discarded = session!;
        session = null;
        Task.Run(discarded.Discard).LogFailures("Discarding a recording");
    }

    public void TogglePause()
    {
        if (Phase != RecordingPhase.Running) return;
        var running = session!;
        if (running.Paused) running.ResumeAsync().LogFailures("Resuming a recording");
        else running.Pause();
        ui.SetFramePaused(running.Paused);
        ui.RefreshToolbar();
    }

    public void SetMicrophone(bool on)
    {
        Microphone = on;
        session?.SetMicrophone(on);
        SaveSettings();
    }

    public void SetSystemSound(bool on)
    {
        SystemSound = on;
        session?.SetSystemSound(on);
        SaveSettings();
    }

    public void SetShowClicks(bool on)
    {
        ShowClicks = on;
        ui.SetClickHighlights(on);
        SaveSettings();
    }

    void SaveSettings() => backend.SaveSettings(new AudioSettings(Microphone, SystemSound, ShowClicks));

    void HideLiveWindows()
    {
        ui.StopClickHighlights();
        ui.HideToolbar();
        ui.HideFrame();
    }
}

/// <summary>Where on the monitor a recording may happen.</summary>
internal static class RecordingArea
{
    // NVENC needs a minimum size.
    public const int MinWidth = 160, MinHeight = 96;

    /// <summary>Clamps the area to the monitor, enforces the minimum size and even dimensions (yuv420p).</summary>
    public static Int32Rect Fit(Int32Rect area, RECT monitor)
    {
        int left = Math.Max(area.X, monitor.left), top = Math.Max(area.Y, monitor.top);
        int width = Math.Max(0, Math.Min(area.X + area.Width, monitor.right) - left);
        int height = Math.Max(0, Math.Min(area.Y + area.Height, monitor.bottom) - top);
        if (width < MinWidth) { left = Math.Max(monitor.left, Math.Min(monitor.right - MinWidth, left + width / 2 - MinWidth / 2)); width = MinWidth; }
        if (height < MinHeight) { top = Math.Max(monitor.top, Math.Min(monitor.bottom - MinHeight, top + height / 2 - MinHeight / 2)); height = MinHeight; }
        return new Int32Rect(left, top, width - width % 2, height - height % 2);
    }
}
