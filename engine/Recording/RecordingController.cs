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
/// <remarks>
/// Every entry point is guarded: an unexpected error (for example in a window) closes the live
/// windows, keeps what was recorded if possible, tells the user and returns to Idle, so the host's
/// hotkey always works again. Saving is bounded by <see cref="saveTimeout"/>, so Busy always recovers.
/// </remarks>
internal sealed class RecordingController
{
    /// <summary>Saving after the ffmpeg calls themselves timed out: only a safety net.</summary>
    static readonly TimeSpan SaveTimeoutMargin = TimeSpan.FromMinutes(5);

    readonly IRecordingUi ui;
    readonly IRecordingBackend backend;
    readonly Func<double, TimeSpan> saveTimeout;
    IRecordingSession? session;
    Action? brokeHandler;            // subscribed to session.Broke; removed when the session is taken
    RECT workArea;

    /// <param name="ui">The windows.</param>
    /// <param name="backend">ffmpeg, monitors, settings.</param>
    /// <param name="saveTimeout">Longest time saving may take for a recording of the given seconds (tests pass a short one).</param>
    public RecordingController(IRecordingUi ui, IRecordingBackend backend, Func<double, TimeSpan>? saveTimeout = null)
    {
        this.ui = ui;
        this.backend = backend;
        this.saveTimeout = saveTimeout ?? (seconds => FfmpegTimeouts.ForMedia(seconds) + SaveTimeoutMargin);
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
    public void Toggle() => Guard("Starting or stopping a recording", () =>
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
    });

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

    internal void AreaPicked(Int32Rect area, RECT monitor, RECT work) => Guard("Starting a recording", () => StartCounting(area, monitor, work));

    void StartCounting(Int32Rect area, RECT monitor, RECT work)
    {
        workArea = work;
        area = RecordingArea.Fit(area, monitor);
        var lookup = backend.FindOutput(monitor, out int output, out RECT desktop);
        if (lookup != OutputLookup.Found)
        {
            Phase = RecordingPhase.Idle;
            ui.ShowProblem(UiText.Recording.NotPossible, lookup == OutputLookup.OtherGpu ? UiText.Recording.MonitorOnOtherGpu : UiText.Recording.MonitorUnreachable, work);
            return;
        }
        try
        {
            session = backend.StartSession(area, output, desktop, new AudioSettings(Microphone, SystemSound, ShowClicks));
            var started = session;
            brokeHandler = () => OnSessionBroke(started);
            started.Broke += brokeHandler;
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

    internal void CountdownFinished() => Guard("Showing the recording toolbar", () =>
    {
        if (Phase != RecordingPhase.Counting || session == null) return;
        Phase = RecordingPhase.Running;
        ui.ShowToolbar(session.Area, workArea);
        ui.StartClickHighlights(session.Area, ShowClicks);
    });

    /// <summary>Esc during the countdown.</summary>
    internal void CancelCountdown() => Guard("Cancelling the countdown", () =>
    {
        if (Phase != RecordingPhase.Counting) return;
        Phase = RecordingPhase.Idle;
        ui.HideFrame();
        DiscardInBackground(TakeSession());
    });

    // ffmpeg exited on its own (e.g. graphics driver, locked screen): save what we have.
    void OnSessionBroke(IRecordingSession broken) => ui.Post(() => Guard("Saving after ffmpeg stopped", () =>
    {
        if (!ReferenceEquals(session, broken)) return;   // an earlier session: already saved or discarded
        if (Phase == RecordingPhase.Counting)
        {
            ui.StopCountdown();
            Phase = RecordingPhase.Running;
        }
        StopAsync().LogFailures("Saving a recording");
    }));

    /// <summary>Stops and saves; completes when the video is saved (or failed), at the latest after the save timeout.</summary>
    public async Task StopAsync()
    {
        if (Phase != RecordingPhase.Running) return;
        Phase = RecordingPhase.Saving;
        var finished = TakeSession()!;
        var work = workArea;
        try
        {
            HideLiveWindows();
            ui.ShowSaving(finished.Elapsed, work);
        }
        catch (Exception ex)   // the windows failed, the recording did not: keep saving
        {
            ErrorLog.Write("Hiding the recording windows", ex);
            ui.CloseLiveWindows();
        }
        await SaveAsync(finished, work);
    }

    async Task SaveAsync(IRecordingSession finished, RECT work)
    {
        RecordedVideo? video = null;
        string reason;
        try
        {
            string path = backend.NewRecordingPath(finished.Begun);
            var saving = Task.Run(() => finished.Finish(path));
            if (await Task.WhenAny(saving, Task.Delay(saveTimeout(finished.Elapsed))) == saving)
            {
                video = await saving;
                reason = finished.Reason;
            }
            else
            {
                saving.LogFailures("Saving a recording (after the timeout)");
                ErrorLog.Write("Saving a recording took too long", null);
                reason = UiText.Common.FfmpegTimedOut;
            }
        }
        catch (Exception ex)   // must always get back to Idle and tell the user
        {
            ErrorLog.Write("Saving a recording", ex);
            reason = finished.Reason;
        }
        Phase = RecordingPhase.Idle;
        try
        {
            if (video == null)
            {
                ui.ShowProblem(UiText.Recording.Failed, reason, work);
                return;
            }
            bool copied = await ui.CopyFileToClipboardAsync(video.Path);
            string heading = finished.AudioLost ? UiText.Recording.SavedWithoutAudio : copied ? UiText.Recording.Saved : UiText.Recording.SavedNotCopied;
            ui.ShowRecording(video, heading, work);
        }
        catch (Exception ex)   // the card failed: the video is saved anyway
        {
            ErrorLog.Write("Showing a saved recording", ex);
        }
    }

    public void Discard() => Guard("Discarding a recording", () =>
    {
        if (Phase != RecordingPhase.Running) return;
        Phase = RecordingPhase.Idle;
        var discarded = TakeSession();
        HideLiveWindows();
        DiscardInBackground(discarded);
    });

    public void TogglePause() => Guard("Pausing or resuming a recording", () =>
    {
        if (Phase != RecordingPhase.Running) return;
        var running = session!;
        if (running.Paused) running.ResumeAsync().LogFailures("Resuming a recording");
        else running.Pause();
        ui.SetFramePaused(running.Paused);
        ui.RefreshToolbar();
    });

    public void SetMicrophone(bool on) => Guard("Switching the microphone", () =>
    {
        Microphone = on;
        session?.SetMicrophone(on);
        SaveSettings();
    });

    public void SetSystemSound(bool on) => Guard("Switching the system sound", () =>
    {
        SystemSound = on;
        session?.SetSystemSound(on);
        SaveSettings();
    });

    public void SetShowClicks(bool on) => Guard("Switching the click highlights", () =>
    {
        ShowClicks = on;
        ui.SetClickHighlights(on);
        SaveSettings();
    });

    void SaveSettings() => backend.SaveSettings(new AudioSettings(Microphone, SystemSound, ShowClicks));

    void HideLiveWindows()
    {
        ui.StopClickHighlights();
        ui.HideToolbar();
        ui.HideFrame();
    }

    IRecordingSession? TakeSession()
    {
        var taken = session;
        session = null;
        if (taken != null && brokeHandler != null) taken.Broke -= brokeHandler;
        brokeHandler = null;
        return taken;
    }

    static void DiscardInBackground(IRecordingSession? discarded)
    {
        if (discarded != null) Task.Run(discarded.Discard).LogFailures("Discarding a recording");
    }

    void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Recover(what, ex); }   // last line of defence for the flow: see Recover
    }

    // Unexpected error: close the live windows, keep what was recorded, tell the user, end in Idle.
    void Recover(string what, Exception error)
    {
        ErrorLog.Write(what, error);
        var phase = Phase;
        var orphan = TakeSession();
        try { ui.CloseLiveWindows(); }
        catch (Exception ex) { ErrorLog.Write("Closing the recording windows", ex); }
        if (phase == RecordingPhase.Saving) return;   // saving runs on and ends in Idle by itself
        if (phase == RecordingPhase.Running && orphan != null)
        {
            Phase = RecordingPhase.Saving;
            SaveAsync(orphan, workArea).LogFailures("Saving a recording after an error");
            return;
        }
        Phase = RecordingPhase.Idle;
        DiscardInBackground(orphan);
        try { ui.ShowProblem(UiText.Recording.Aborted, error.Message, workArea); }
        catch (Exception ex) { ErrorLog.Write("Showing a recording error", ex); }
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
