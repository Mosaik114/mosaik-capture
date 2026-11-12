
namespace MosaikCapture.Engine.Tests.Recording;

// The recording flow Idle → Picking → Counting → Running → Saving → Idle with fake windows, ffmpeg and sessions.
public sealed class RecordingControllerTests
{
    static readonly RECT Monitor = new() { left = 0, top = 0, right = 1920, bottom = 1080 };
    static readonly RECT Work = new() { left = 0, top = 0, right = 1920, bottom = 1040 };
    static readonly Int32Rect Picked = new(101, 51, 641, 361);
    static readonly Int32Rect Fitted = new(101, 51, 640, 360);

    readonly FakeUi ui = new();
    readonly FakeBackend backend = new();
    readonly RecordingController controller;

    public RecordingControllerTests() => controller = new RecordingController(ui, backend, _ => saveTimeout);

    TimeSpan saveTimeout = TimeSpan.FromSeconds(10);

    static async Task Within(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(finished == task, "timed out");
        await task;
    }

    /// <summary>Picks the area and lets the countdown run out: the recording is running.</summary>
    async Task<FakeSession> StartRunning()
    {
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        await controller.LaunchAsync();
        ui.CountdownFinished!();
        Assert.Equal(RecordingPhase.Running, controller.Phase);
        return backend.Sessions.Single();
    }

    [Fact]
    public async Task FullRecordingGoesThroughAllPhasesAndEndsIdle()
    {
        backend.Settings = new AudioSettings(microphone: true, systemSound: false, showClicks: true);

        controller.Toggle();
        Assert.Equal(RecordingPhase.Picking, controller.Phase);
        Assert.True(controller.Busy);
        Assert.False(controller.Live);
        Assert.True(controller.Microphone);
        Assert.False(controller.SystemSound);
        Assert.Equal(new[] { "LoadSettings", "RemoveLeftovers", "PrepareEncoder" }, backend.Calls);

        ui.Pick!(Picked, Monitor, Work);
        Assert.Equal(RecordingPhase.Counting, controller.Phase);
        Assert.True(controller.Live);
        var session = backend.Sessions.Single();
        Assert.Equal(Fitted, session.Area);
        Assert.Equal(new AudioSettings(true, false, true), backend.StartedWith);
        Assert.Contains("ShowFrame " + Fitted, ui.Calls);
        Assert.Equal(0, session.Resumes);

        ui.Launch!();
        Assert.Equal(1, session.Resumes);
        ui.CountdownFinished!();
        Assert.Equal(RecordingPhase.Running, controller.Phase);
        Assert.Contains("ShowToolbar " + Fitted, ui.Calls);
        Assert.Contains("StartClickHighlights True", ui.Calls);

        session.Video = new RecordedVideo { Path = "C:\\Videos\\out.mp4", Duration = 3 };
        await Within(controller.StopAsync());

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.False(controller.Busy);
        Assert.Equal("C:\\Videos\\out.mp4", session.FinishedInto);
        Assert.Equal(new[] { "StopClickHighlights", "HideToolbar", "HideFrame", "ShowSaving", "CopyFileToClipboard C:\\Videos\\out.mp4", "ShowRecording Aufnahme gespeichert" },
            ui.Calls.SkipWhile(c => c != "StopClickHighlights"));
    }

    [Fact]
    public async Task PhaseIsSavingWhileTheSegmentsAreJoined()
    {
        var session = await StartRunning();
        session.HoldFinish();

        var stopping = controller.StopAsync();
        Assert.Equal(RecordingPhase.Saving, controller.Phase);
        Assert.True(controller.Busy);
        Assert.False(controller.Live);
        controller.Toggle();        // the hotkey during saving does nothing
        controller.TogglePause();
        controller.Discard();
        Assert.Equal(RecordingPhase.Saving, controller.Phase);

        session.ReleaseFinish();
        await Within(stopping);
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.False(session.Discarded.Task.IsCompleted);
    }

    [Fact]
    public async Task ToggleWhileRunningStopsAndSaves()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };

        controller.Toggle();

        await Within(ui.Finished.Task);
        Assert.Equal("ShowRecording Aufnahme gespeichert", ui.Calls.Last());
    }

    [Fact]
    public void WithoutFfmpegNothingStartsAndTheUserIsTold()
    {
        backend.FfmpegAvailable = false;

        controller.Toggle();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Null(ui.Pick);
        Assert.Equal(new[] { "ShowProblem ffmpeg fehlt | Für Aufnahmen: winget install ffmpeg" }, ui.Calls.Where(c => c.StartsWith("Show", StringComparison.Ordinal)));
    }

    [Fact]
    public void CancelledPickReturnsToIdle()
    {
        controller.Toggle();

        ui.PickCancelled!();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Empty(backend.Sessions);
    }

    [Fact]
    public void MonitorWithoutDesktopDuplicationCannotBeRecorded()
    {
        backend.HasOutput = false;
        controller.Toggle();

        ui.Pick!(Picked, Monitor, Work);

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Empty(backend.Sessions);
        Assert.Equal("ShowProblem Aufnahme nicht möglich | Dieser Monitor ist für die Aufnahme nicht erreichbar", ui.Calls.Last());
    }

    [Fact]
    public void SessionThatCannotStartIsReportedAndLeavesIdle()
    {
        backend.StartFailure = new IOException("Zugriff verweigert");
        controller.Toggle();

        ui.Pick!(Picked, Monitor, Work);

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Equal("ShowProblem Aufnahme nicht möglich | Zugriff verweigert", ui.Calls.Last());
        Assert.DoesNotContain(ui.Calls, c => c.StartsWith("ShowFrame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EscDuringTheCountdownDiscardsTheSession()
    {
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        var session = backend.Sessions.Single();

        ui.CountdownCancelled!();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Contains("HideFrame", ui.Calls);
        await Within(session.Discarded.Task);
        ui.CountdownFinished!();   // a late tick of the countdown must not start anything
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task HotkeyDuringTheCountdownCancelsIt()
    {
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        var session = backend.Sessions.Single();

        controller.Toggle();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Contains("StopCountdown", ui.Calls);
        await Within(session.Discarded.Task);
    }

    [Fact]
    public async Task FfmpegDyingDuringTheCountdownSavesWhatThereIs()
    {
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        var session = backend.Sessions.Single();
        session.Video = null;
        session.Reason = "Device lost";

        session.Break();

        await Within(ui.Finished.Task);
        Assert.Contains("StopCountdown", ui.Calls);
        Assert.Equal("ShowProblem Aufnahme fehlgeschlagen | Device lost", ui.Calls.Last());
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task FfmpegDyingWhileRunningSavesTheRecording()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };

        session.Break();

        await Within(ui.Finished.Task);
        Assert.Equal("ShowRecording Aufnahme gespeichert", ui.Calls.Last());
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task DiscardHidesEverythingAndDeletesTheRecording()
    {
        var session = await StartRunning();

        controller.Discard();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Equal(new[] { "StopClickHighlights", "HideToolbar", "HideFrame" }, ui.Calls.Skip(ui.Calls.Count - 3));
        await Within(session.Discarded.Task);
        Assert.Null(session.FinishedInto);
    }

    [Fact]
    public async Task PauseAndResume()
    {
        var session = await StartRunning();

        controller.TogglePause();
        Assert.True(controller.Paused);
        Assert.Equal(new[] { "SetFramePaused True", "RefreshToolbar" }, ui.Calls.Skip(ui.Calls.Count - 2));

        controller.TogglePause();
        Assert.False(controller.Paused);
        Assert.Equal(2, session.Resumes);
        Assert.Equal(new[] { "SetFramePaused False", "RefreshToolbar" }, ui.Calls.Skip(ui.Calls.Count - 2));
    }

    [Fact]
    public async Task FailedAssemblyShowsTheReason()
    {
        var session = await StartRunning();
        session.Video = null;
        session.Reason = "Conversion failed!";

        await Within(controller.StopAsync());

        Assert.Equal("ShowProblem Aufnahme fehlgeschlagen | Conversion failed!", ui.Calls.Last());
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task CrashWhileSavingStillEndsIdleAndTellsTheUser()
    {
        var session = await StartRunning();
        session.FinishFailure = new InvalidOperationException("boom");
        session.Reason = "Die Aufnahme wurde unerwartet beendet.";

        await Within(controller.StopAsync());

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Equal("ShowProblem Aufnahme fehlgeschlagen | Die Aufnahme wurde unerwartet beendet.", ui.Calls.Last());
    }

    [Fact]
    public async Task LostAudioIsMentionedInTheHeading()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };
        session.AudioLost = true;

        await Within(controller.StopAsync());

        Assert.Equal("ShowRecording Aufnahme ohne Ton gespeichert", ui.Calls.Last());
    }

    [Fact]
    public async Task AudioChoicesAreForwardedAndSaved()
    {
        var session = await StartRunning();

        controller.SetMicrophone(false);
        controller.SetSystemSound(true);
        controller.SetShowClicks(false);

        Assert.Equal(new[] { false, true }, new[] { session.Microphone, session.SystemSound });
        Assert.Equal("SetClickHighlights False", ui.Calls.Last());
        Assert.Equal(new AudioSettings(false, true, false), backend.Saved.Last());
        Assert.Equal(3, backend.Saved.Count);
    }

    [Fact]
    public async Task BusyClipboardIsNotHiddenBehindASuccessHeading()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };
        ui.ClipboardWorks = false;

        await Within(controller.StopAsync());

        Assert.Equal("ShowRecording Aufnahme gespeichert, nicht kopiert", ui.Calls.Last());
    }

    [Fact]
    public async Task SavingThatHangsEndsAfterTheTimeoutAndBusyRecovers()
    {
        saveTimeout = TimeSpan.FromMilliseconds(200);
        var session = await StartRunning();
        session.HoldFinish();

        await Within(controller.StopAsync());

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.False(controller.Busy);
        Assert.Equal("ShowProblem Aufnahme fehlgeschlagen | " + UiText.Common.FfmpegTimedOut, ui.Calls.Last());
        session.ReleaseFinish();
    }

    [Fact]
    public void WindowErrorWhilePickingResetsToIdleAndTellsTheUser()
    {
        ui.FailOn = "ShowFrame";
        controller.Toggle();

        ui.Pick!(Picked, Monitor, Work);

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Equal(new[] { "CloseLiveWindows", "ShowProblem Aufnahme abgebrochen | window broke" }, ui.Calls.Skip(ui.Calls.Count - 2));
    }

    [Fact]
    public async Task WindowErrorWhenTheToolbarAppearsSavesWhatWasRecorded()
    {
        ui.FailOn = "ShowToolbar";
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        var session = backend.Sessions.Single();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };
        await controller.LaunchAsync();

        ui.CountdownFinished!();

        await Within(ui.Finished.Task);
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Contains("CloseLiveWindows", ui.Calls);
        Assert.Equal("ShowRecording Aufnahme gespeichert", ui.Calls.Last());
    }

    [Fact]
    public async Task WindowErrorDuringTheCountdownDiscardsTheSession()
    {
        controller.Toggle();
        ui.FailOn = "StartCountdown";
        ui.Pick!(Picked, Monitor, Work);
        var session = backend.Sessions.Single();

        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Contains("CloseLiveWindows", ui.Calls);
        await Within(session.Discarded.Task);
    }

    [Fact]
    public async Task WindowErrorWhileRecordingStillSavesTheVideo()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };
        ui.FailOn = "SetFramePaused";

        controller.TogglePause();

        await Within(ui.Finished.Task);
        Assert.Equal(RecordingPhase.Idle, controller.Phase);
        Assert.Equal("C:\\Videos\\out.mp4", session.FinishedInto);
        Assert.Equal("ShowRecording Aufnahme gespeichert", ui.Calls.Last());
        Assert.False(session.Discarded.Task.IsCompleted);
    }

    [Fact]
    public async Task HotkeyWorksAgainAfterAnError()
    {
        ui.FailOn = "ShowFrame";
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        ui.FailOn = null;

        await StartRunningAgain();

        Assert.Equal(RecordingPhase.Running, controller.Phase);
    }

    async Task StartRunningAgain()
    {
        controller.Toggle();
        ui.Pick!(Picked, Monitor, Work);
        await controller.LaunchAsync();
        ui.CountdownFinished!();
    }

    [Fact]
    public async Task HidingWindowsFailsButSavingGoesOn()
    {
        var session = await StartRunning();
        session.Video = new RecordedVideo { Path = "C:\\x.mp4" };
        ui.FailOn = "HideToolbar";

        await Within(controller.StopAsync());

        Assert.Contains("CloseLiveWindows", ui.Calls);
        Assert.Equal("ShowRecording Aufnahme gespeichert", ui.Calls.Last());
    }

    [Fact]
    public void MicrophoneLevelOnlyWhileTheMicrophoneIsOn()
    {
        Assert.Equal(0, controller.MicrophoneLevel);
        Assert.Equal(0, controller.Elapsed);
        Assert.False(controller.Paused);
    }

    // ---------- Fakes ----------

    sealed class FakeUi : IRecordingUi
    {
        public readonly List<string> Calls = new();
        public readonly TaskCompletionSource<bool> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<Int32Rect, RECT, RECT>? Pick;
        public Action? PickCancelled, Launch, CountdownFinished, CountdownCancelled;

        public string? FailOn;
        public bool ClipboardWorks = true;

        void Log(string call)
        {
            lock (Calls) Calls.Add(call);
            if (FailOn != null && call.StartsWith(FailOn, StringComparison.Ordinal)) throw new InvalidOperationException("window broke");
        }

        public RECT WorkAreaAtCursor() => Work;

        public void PickArea(Action<Int32Rect, RECT, RECT> picked, Action cancelled)
        {
            Pick = picked;
            PickCancelled = cancelled;
        }

        public void ShowFrame(Int32Rect area, RECT monitor) => Log("ShowFrame " + area);
        public void HideFrame() => Log("HideFrame");
        public void SetFramePaused(bool paused) => Log("SetFramePaused " + paused);

        public void StartCountdown(Int32Rect area, RECT monitor, Action launch, Action finished, Action cancelled)
        {
            Log("StartCountdown");
            Launch = launch;
            CountdownFinished = finished;
            CountdownCancelled = cancelled;
        }

        public void StopCountdown() => Log("StopCountdown");
        public void ShowToolbar(Int32Rect area, RECT work) => Log("ShowToolbar " + area);
        public void RefreshToolbar() => Log("RefreshToolbar");
        public void HideToolbar() => Log("HideToolbar");
        public void StartClickHighlights(Int32Rect area, bool enabled) => Log("StartClickHighlights " + enabled);
        public void SetClickHighlights(bool enabled) => Log("SetClickHighlights " + enabled);
        public void StopClickHighlights() => Log("StopClickHighlights");
        public void ShowSaving(double seconds, RECT work) => Log("ShowSaving");

        public void ShowProblem(string heading, string reason, RECT work)
        {
            Log("ShowProblem " + heading + " | " + reason);
            Finished.TrySetResult(true);
        }

        public void ShowRecording(RecordedVideo video, string heading, RECT work)
        {
            Log("ShowRecording " + heading);
            Finished.TrySetResult(true);
        }

        public Task<bool> CopyFileToClipboardAsync(string path)
        {
            Log("CopyFileToClipboard " + path);
            return Task.FromResult(ClipboardWorks);
        }

        public void CloseLiveWindows() => Log("CloseLiveWindows");

        public void Post(Action action) => action();
    }

    sealed class FakeBackend : IRecordingBackend
    {
        public readonly List<string> Calls = new();
        public readonly List<FakeSession> Sessions = new();
        public readonly List<AudioSettings> Saved = new();
        public bool HasOutput = true;
        public Exception? StartFailure;
        public AudioSettings Settings = new(false, true, true);
        public AudioSettings? StartedWith;

        public bool FfmpegAvailable { get; set; } = true;

        public void PrepareEncoder() => Calls.Add("PrepareEncoder");

        public void RemoveLeftovers() => Calls.Add("RemoveLeftovers");

        public bool TryFindOutput(RECT monitor, out int output, out RECT desktop)
        {
            output = 0;
            desktop = monitor;
            return HasOutput;
        }

        public IRecordingSession StartSession(Int32Rect area, int output, RECT desktop, AudioSettings audio)
        {
            if (StartFailure != null) throw StartFailure;
            StartedWith = audio;
            var session = new FakeSession(area, audio);
            Sessions.Add(session);
            return session;
        }

        public string NewRecordingPath(DateTime begun) => "C:\\Videos\\out.mp4";

        public AudioSettings LoadSettings()
        {
            Calls.Add("LoadSettings");
            return Settings;
        }

        public void SaveSettings(AudioSettings settings) => Saved.Add(settings);
    }

    sealed class FakeSession : IRecordingSession
    {
        public readonly TaskCompletionSource<bool> Discarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task finishAllowed = Task.CompletedTask;
        TaskCompletionSource<bool>? finishGate;
        public RecordedVideo? Video;
        public Exception? FinishFailure;
        public string? FinishedInto;
        public int Resumes;
        public bool Microphone, SystemSound;

        public FakeSession(Int32Rect area, AudioSettings audio)
        {
            Area = area;
            Microphone = audio.Microphone;
            SystemSound = audio.SystemSound;
        }

        public Int32Rect Area { get; }
        public DateTime Begun { get; } = DateTime.Now;
        public string Reason { get; set; } = "";
        public bool Paused { get; private set; } = true;
        public double Elapsed => 0;
        public float MicrophoneLevel => 0.5f;
        public bool AudioLost { get; set; }

        public event Action? Broke;

        public void Break() => Broke?.Invoke();

        public void HoldFinish()
        {
            finishGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            finishAllowed = finishGate.Task;
        }

        public void ReleaseFinish() => finishGate!.TrySetResult(true);

        public Task ResumeAsync()
        {
            Resumes++;
            Paused = false;
            return Task.CompletedTask;
        }

        public void Pause() => Paused = true;

        public void SetMicrophone(bool on) => Microphone = on;

        public void SetSystemSound(bool on) => SystemSound = on;

        public RecordedVideo? Finish(string path)
        {
            Assert.True(finishAllowed.Wait(TimeSpan.FromSeconds(10)), "finish was never released");
            if (FinishFailure != null) throw FinishFailure;
            FinishedInto = path;
            return Video;
        }

        public void Discard() => Discarded.TrySetResult(true);
    }
}
