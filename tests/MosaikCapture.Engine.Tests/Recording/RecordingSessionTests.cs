using System.ComponentModel;

namespace MosaikCapture.Engine.Tests.Recording;

// RecordingSession with a fake ffmpeg and fake audio: segments, pause/resume races, probing and assembling.
public sealed class RecordingSessionTests : IDisposable
{
    static readonly RECT Desktop = new() { left = 0, top = 0, right = 1920, bottom = 1080 };
    static readonly Int32Rect Area = new(100, 50, 640, 360);

    readonly TempDirectory root = new();
    readonly FakeRunner runner = new();
    readonly FakeAudio audio = new();
    readonly List<Point> nudges = new();
    Func<Task<VideoEncoder>> encoder = async () =>
    {
        await Task.Yield();   // like the real detection: the resume continues on a pool thread
        return VideoEncoder.Software;
    };

    public void Dispose() => root.Dispose();

    RecordingSession NewSession() => new(Area, 0, Desktop, new AudioSettings(false, true, false), 30, new RecordingSessionServices
    {
        Runner = runner,
        CreateAudio = (_, _) => audio,
        WorkRoot = root.Path,
        Encoder = () => encoder(),
        NudgeFirstFrame = (x, y) => { lock (nudges) nudges.Add(new Point(x, y)); },
        LoadVideo = (path, _) => Task.FromResult(new RecordedVideo { Path = path, Duration = 4 }),
        PreviousSegmentTimeout = TimeSpan.FromSeconds(5),
    });

    static async Task Within(Task task)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == task, "timed out");
        await task;
    }

    [Fact]
    public async Task ResumeStartsASegmentAndNudgesTheFirstFrame()
    {
        var session = NewSession();
        Assert.True(session.Paused);

        await Within(session.ResumeAsync());

        Assert.False(session.Paused);
        Assert.Equal(1, session.SegmentCount);
        Assert.EndsWith("segment-01.mkv", runner.Started.Single().Arguments.LastItem());
        Assert.Equal(new[] { new Point(100 + 640 - 2, 50 + 360 - 2) }, nudges);
    }

    [Fact]
    public async Task PauseAsksFfmpegToQuitAndTheNextSegmentWaitsForIt()
    {
        var session = NewSession();
        await Within(session.ResumeAsync());
        var first = runner.Processes.Single();

        session.Pause();
        Assert.True(session.Paused);
        await Within(first.Quit.Task);
        await Within(session.ResumeAsync());

        Assert.Equal(2, session.SegmentCount);
        Assert.True(first.Disposed);
        Assert.EndsWith("segment-02.mkv", runner.Started[runner.Started.Count - 1].Arguments.LastItem());
    }

    [Fact]
    public async Task PauseWhileTheResumeWaitsCancelsTheResume()
    {
        var pending = new TaskCompletionSource<VideoEncoder>(TaskCreationOptions.RunContinuationsAsynchronously);
        encoder = () => pending.Task;
        var session = NewSession();

        var resume = session.ResumeAsync();
        session.Pause();
        pending.SetResult(VideoEncoder.Software);
        await Within(resume);

        Assert.Equal(0, session.SegmentCount);
        Assert.Empty(runner.Started);
        Assert.True(session.Paused);
    }

    [Fact]
    public async Task PauseWhileFfmpegStartsStopsTheNewProcessAtOnce()
    {
        // The fake ffmpeg start blocks until released. Before, the resume continued wherever the encoder task
        // completed; when that was the thread the test itself needed to continue on (xUnit's context), the test
        // could not call Pause until the start timed out. Now the resume is driven onto a dedicated thread: the
        // encoder result is set from that thread, and the session's ConfigureAwait(false) continuation, with the
        // blocking start, runs right there, while the test continues on its own thread. (The test must not
        // join that thread: its own continuation may run on it after the resume completes.)
        var encoderGate = new TaskCompletionSource<VideoEncoder>();   // no RunContinuationsAsynchronously: continue inline
        encoder = () => encoderGate.Task;
        runner.StartGate = new ManualResetEventSlim();
        var session = NewSession();

        var resume = session.ResumeAsync();   // now waits for the encoder (the continuation is registered)
        var starter = new Thread(() => encoderGate.SetResult(VideoEncoder.Software)) { IsBackground = true, Name = "Fake ffmpeg start" };
        starter.Start();
        await Within(runner.StartEntered.Task);   // ffmpeg is being "started" on that thread, outside the session lock
        session.Pause();
        runner.StartGate.Set();
        await Within(resume);

        await Within(runner.Processes.Single().Quit.Task);
        Assert.Empty(nudges);
        Assert.True(session.Paused);
    }

    [Fact]
    public async Task FinishWaitsForAnFfmpegThatIsStillStarting()
    {
        // same set-up as above: the resume runs on a dedicated thread and blocks in the fake start
        var encoderGate = new TaskCompletionSource<VideoEncoder>();
        encoder = () => encoderGate.Task;
        runner.StartGate = new ManualResetEventSlim();
        var session = NewSession();
        var resume = session.ResumeAsync();
        new Thread(() => encoderGate.SetResult(VideoEncoder.Software)) { IsBackground = true }.Start();
        await Within(runner.StartEntered.Task);

        var finishing = session.FinishAsync(root.File("out.mp4"), TestContext.Current.CancellationToken);   // stop right away
        await Task.Yield();
        Assert.False(finishing.IsCompleted, "Finish must wait for the segment whose ffmpeg is still starting");
        Assert.Empty(runner.Runs);   // nothing probed while that ffmpeg may still write

        runner.StartGate.Set();
        await Within(resume);
        var video = await WithinResult(finishing);

        var process = runner.Processes.Single();
        Assert.True(process.Quit.Task.IsCompleted, "the late ffmpeg was asked to quit");
        Assert.True(process.Disposed);
        Assert.NotNull(video);
        Assert.Contains(runner.Runs, run => run.Program == FfmpegProgram.Probe);
    }

    [Fact]
    public async Task PausedShowsTheIntentEvenWhileTheResumeIsPending()
    {
        var pending = new TaskCompletionSource<VideoEncoder>(TaskCreationOptions.RunContinuationsAsynchronously);
        encoder = () => pending.Task;
        var session = NewSession();

        var resume = session.ResumeAsync();
        Assert.False(session.Paused);   // resumed: shown as running while ffmpeg starts
        session.Pause();
        Assert.True(session.Paused);    // paused at once, not only when the resume gives up
        pending.SetResult(VideoEncoder.Software);
        await Within(resume);
        Assert.True(session.Paused);
    }

    [Fact]
    public async Task PauseThenResumeWhileTheFirstResumeWaitsEndsRecording()
    {
        var pending = new TaskCompletionSource<VideoEncoder>(TaskCreationOptions.RunContinuationsAsynchronously);
        encoder = () => pending.Task;
        var session = NewSession();

        var first = session.ResumeAsync();
        session.Pause();
        var second = session.ResumeAsync();   // finds the first resume still busy and leaves it to that one
        pending.SetResult(VideoEncoder.Software);
        await Within(Task.WhenAll(first, second));

        Assert.False(session.Paused);
        Assert.Equal(1, session.SegmentCount);
        Assert.Single(runner.Processes);
    }

    [Fact]
    public async Task PauseResumePauseWhileWaitingEndsPaused()
    {
        var pending = new TaskCompletionSource<VideoEncoder>(TaskCreationOptions.RunContinuationsAsynchronously);
        encoder = () => pending.Task;
        var session = NewSession();

        var first = session.ResumeAsync();
        session.Pause();
        var second = session.ResumeAsync();
        session.Pause();
        pending.SetResult(VideoEncoder.Software);
        await Within(Task.WhenAll(first, second));

        Assert.True(session.Paused);
        Assert.Equal(0, session.SegmentCount);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(31, false)]
    [InlineData(40, true)]
    public async Task RapidTogglesEndInTheLastIntent(int toggles, bool paused)
    {
        var session = NewSession();
        var resumes = new List<Task>();

        for (int i = 0; i < toggles; i++)
        {
            if (i % 2 == 0) resumes.Add(session.ResumeAsync());
            else session.Pause();
        }
        await Within(Task.WhenAll(resumes));
        List<FakeProcess> started;
        lock (runner.Processes) started = runner.Processes.ToList();
        // every segment but the one still wanted is asked to quit (in the background): wait for exactly those signals
        var stopped = paused ? started : started.Take(started.Count - 1);
        await Within(Task.WhenAll(stopped.Select(p => p.Quit.Task)));

        Assert.Equal(paused, session.Paused);
        if (!paused) Assert.False(started[started.Count - 1].Quit.Task.IsCompleted, "the wanted segment was stopped");
    }

    [Fact]
    public async Task ElapsedNeverDipsAcrossPauses()
    {
        var session = NewSession();
        using var stop = new CancellationTokenSource();
        double lowest = 0;
        bool dipped = false;
        // a dedicated thread, not a pool thread: the busy reader must not slow down the session's own continuations
        var reader = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                double now = session.Elapsed;
                if (now < lowest) dipped = true;
                lowest = Math.Max(lowest, now);
            }
        })
        { IsBackground = true };
        reader.Start();

        for (int i = 0; i < 50; i++)
        {
            await Within(session.ResumeAsync());
            session.Pause();
        }
        stop.Cancel();
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "the reader did not stop");

        Assert.False(dipped, "Elapsed went backwards while pausing");
    }

    [Fact]
    public async Task AnOlderSegmentEndingAfterAResumeIsNoBreak()
    {
        var session = NewSession();
        int broke = 0;
        session.Broke += () => broke++;
        await Within(session.ResumeAsync());
        var first = runner.Processes.Single();
        session.Pause();
        await Within(session.ResumeAsync());

        first.Exit("late exit of the paused segment");

        Assert.Equal(0, broke);
        Assert.False(session.Paused);
    }

    [Fact]
    public async Task OnlyOneResumeAtATime()
    {
        var session = NewSession();

        await Within(Task.WhenAll(session.ResumeAsync(), session.ResumeAsync(), session.ResumeAsync()));

        Assert.Equal(1, session.SegmentCount);
    }

    [Fact]
    public async Task FfmpegThatCannotStartBreaksWithTheReasonOutsideTheLock()
    {
        runner.StartFailure = new Win32Exception("ffmpeg.exe is missing");
        var session = NewSession();
        bool lockFree = false;
        session.Broke += () =>
        {
            // a dedicated thread (not the pool) tries to take the session lock while Broke is being raised
            var probe = new Thread(() => _ = session.SegmentCount) { IsBackground = true };
            probe.Start();
            lockFree = probe.Join(TimeSpan.FromSeconds(2));
        };

        await Within(session.ResumeAsync());

        Assert.True(lockFree, "Broke was raised while the session lock was held");
        Assert.Equal("ffmpeg.exe is missing", session.Reason);
        Assert.Equal(0, session.SegmentCount);
        Assert.True(session.Paused);
    }

    [Fact]
    public async Task StartFailingAfterTheProcessExistedIsRolledBack()
    {
        runner.StartFailure = new IOException("pipe broken while starting");
        var session = NewSession();
        int broke = 0;
        session.Broke += () => broke++;

        await Within(session.ResumeAsync());

        Assert.Equal(0, session.SegmentCount);
        Assert.True(session.Paused);
        Assert.Equal(1, broke);
        Assert.Equal("pipe broken while starting", session.Reason);
    }

    [Fact]
    public async Task UnexpectedStartErrorIsRolledBackAndSurfaces()
    {
        runner.StartFailure = new NullReferenceException("bug");
        var session = NewSession();

        await Assert.ThrowsAsync<NullReferenceException>(() => session.ResumeAsync());

        Assert.Equal(0, session.SegmentCount);
        Assert.True(session.Paused);
        runner.StartFailure = null;
        await Within(session.ResumeAsync());   // and the session still works
        Assert.Equal(1, session.SegmentCount);
    }

    [Fact]
    public async Task FfmpegEndingOnItsOwnBreaksWithItsLastErrorLine()
    {
        var session = NewSession();
        int broke = 0;
        session.Broke += () => broke++;
        await Within(session.ResumeAsync());

        runner.Processes.Single().Exit("[ddagrab] something", "Device lost");

        Assert.Equal(1, broke);
        Assert.Equal("Device lost", session.Reason);
    }

    [Fact]
    public async Task PausedFfmpegEndingIsNoBreak()
    {
        var session = NewSession();
        int broke = 0;
        session.Broke += () => broke++;
        await Within(session.ResumeAsync());
        session.Pause();

        runner.Processes.Single().Exit("exiting normally");

        Assert.Equal(0, broke);
    }

    [Fact]
    public async Task FinishProbesEverySegmentAndAssemblesThemWithTheSound()
    {
        var session = NewSession();
        await Within(session.ResumeAsync());
        session.Pause();
        await Within(session.ResumeAsync());
        string target = root.File("out.mp4");

        var video = await session.FinishAsync(target, TestContext.Current.CancellationToken);

        Assert.NotNull(video);
        Assert.Equal(target, video!.Path);
        Assert.Equal(2, runner.Runs.Count(r => r.Program == FfmpegProgram.Probe));
        var assemble = runner.Runs.Single(r => r.Program == FfmpegProgram.Ffmpeg).Arguments;
        Assert.Contains(audio.SystemSoundFile, assemble);
        Assert.DoesNotContain(audio.MicrophoneFile, assemble);
        Assert.Equal(2, runner.ConcatLines.Length);
        Assert.False(Directory.Exists(session.WorkDirectory), "the work folder should be removed after success");
        Assert.False(session.AudioLost);
    }

    [Fact]
    public async Task SegmentsWithoutFramesFailWithTheLastFfmpegError()
    {
        runner.ProbedDuration = 0;
        var session = NewSession();
        await Within(session.ResumeAsync());
        runner.Processes.Single().Error("Could not open the output");

        var video = await session.FinishAsync(root.File("out.mp4"), TestContext.Current.CancellationToken);

        Assert.Null(video);
        Assert.Equal("Could not open the output", session.Reason);
        Assert.DoesNotContain(runner.Runs, r => r.Program == FfmpegProgram.Ffmpeg);
    }

    [Fact]
    public async Task FailedAssemblyKeepsTheSegmentsAndRemovesThePartialFile()
    {
        runner.AssembleExitCode = 1;
        var session = NewSession();
        await Within(session.ResumeAsync());
        string target = root.File("out.mp4");

        var video = await session.FinishAsync(target, TestContext.Current.CancellationToken);

        Assert.Null(video);
        Assert.Equal("Conversion failed!", session.Reason);
        Assert.False(File.Exists(target));
        Assert.True(Directory.Exists(session.WorkDirectory));
    }

    [Fact]
    public async Task CancellingTheSaveStopsAHangingAssemblyAndReportsTheTimeout()
    {
        runner.HangOnAssemble = true;
        var session = NewSession();
        await Within(session.ResumeAsync());
        string target = root.File("out.mp4");
        using var cancel = new CancellationTokenSource();

        var saving = session.FinishAsync(target, cancel.Token);
        cancel.Cancel();
        var video = await WithinResult(saving);

        Assert.Null(video);
        Assert.Equal(UiText.Common.FfmpegTimedOut, session.Reason);
        Assert.False(File.Exists(target));
    }

    static async Task<T> WithinResult<T>(Task<T> task)
    {
        await Within(task);
        return await task;
    }

    [Fact]
    public async Task UnfinishedAudioIsLeftOutAndReported()
    {
        audio.StopResult = false;
        var session = NewSession();
        await Within(session.ResumeAsync());

        var video = await session.FinishAsync(root.File("out.mp4"), TestContext.Current.CancellationToken);

        Assert.NotNull(video);
        Assert.True(session.AudioLost);
        Assert.DoesNotContain(audio.SystemSoundFile, runner.Runs.Single(r => r.Program == FfmpegProgram.Ffmpeg).Arguments);
    }

    [Fact]
    public async Task WorkFolderWaitsForTheAudioThreadBeforeItIsRemoved()
    {
        audio.Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession();
        await Within(session.ResumeAsync());

        await Task.Run(session.Discard, TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(session.WorkDirectory), "removed while the WAV files are still open");

        audio.Release.SetResult(true);
        await Within(session.WorkDirectoryRemoved);
        Assert.False(Directory.Exists(session.WorkDirectory));
    }

    // ---------- Fakes ----------

    sealed class FakeProcess : IFfmpegProcess
    {
        readonly Action<string> errorLine;
        readonly Action exited;

        public FakeProcess(Action<string> errorLine, Action exited)
        {
            this.errorLine = errorLine;
            this.exited = exited;
        }

        public TaskCompletionSource<bool> Quit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public void Error(params string[] lines)
        {
            foreach (string line in lines) errorLine(line);
        }

        public void Exit(params string[] lines)
        {
            Error(lines);
            exited();
        }

        public void RequestQuit() => Quit.TrySetResult(true);
        public bool WaitForExit(TimeSpan timeout) => true;
        public void Kill() { }
        public void Dispose() => Disposed = true;
    }

    sealed class FakeRunner : IFfmpegRunner
    {
        public readonly List<FfmpegArgs> Started = new();
        public readonly List<FakeProcess> Processes = new();
        public readonly List<(FfmpegProgram Program, IReadOnlyList<string> Arguments)> Runs = new();
        public readonly TaskCompletionSource<bool> StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Holds Start until set; only used from a dedicated thread, never from the thread pool.</summary>
        public ManualResetEventSlim? StartGate;
        public Exception? StartFailure;
        public double ProbedDuration = 2;
        public int AssembleExitCode;
        public string[] ConcatLines = Array.Empty<string>();

        public IFfmpegProcess Start(FfmpegArgs args, Action<string> errorLine, Action exited)
        {
            StartEntered.TrySetResult(true);
            if (StartGate != null && !StartGate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("the test never released the fake start");
            if (StartFailure != null) throw StartFailure;
            File.WriteAllText(args.Arguments.LastItem(), "frames");
            var process = new FakeProcess(errorLine, exited);
            lock (Processes)
            {
                Started.Add(args);
                Processes.Add(process);
            }
            return process;
        }

        /// <summary>Holds the assembling run until the token fires (a hanging ffmpeg).</summary>
        public bool HangOnAssemble;

        public async Task<FfmpegResult> RunAsync(FfmpegProgram program, FfmpegArgs args, TimeSpan timeout, CancellationToken cancellation)
        {
            lock (Runs) Runs.Add((program, args.Arguments));
            if (program == FfmpegProgram.Probe) return new FfmpegResult(0, "start_time=0.1\nduration=" + FfmpegArgs.Number(ProbedDuration) + "\n", "");
            ConcatLines = File.ReadAllLines(args.Arguments[args.Arguments.ToList().IndexOf("-i") + 1]);
            if (HangOnAssemble) await Task.Delay(Timeout.Infinite, cancellation);   // like Ffmpeg.RunAsync: killed and cancelled
            if (AssembleExitCode != 0)
            {
                File.WriteAllText(args.Arguments.LastItem(), "partial");
                return new FfmpegResult(AssembleExitCode, "", "Conversion failed!");
            }
            File.WriteAllText(args.Arguments.LastItem(), "video");
            return new FfmpegResult(0, "", "");
        }
    }

    sealed class FakeAudio : IAudioRecorder
    {
        public bool StopResult = true;
        public TaskCompletionSource<bool>? Release;

        public string SystemSoundFile => "C:\\work\\system.wav";
        public string MicrophoneFile => "C:\\work\\microphone.wav";
        public bool SystemSoundUsed => true;
        public bool MicrophoneUsed => false;
        public float MicrophoneLevel => 0;
        public Task Finished => Release?.Task ?? Task.CompletedTask;
        public void Start() { }
        public bool Stop() => StopResult;
        public double Offset(long ticks) => 0.25;
        public void SetMicrophone(bool on) { }
        public void SetSystemSound(bool on) { }
    }
}

internal static class ListExtensions
{
    public static T LastItem<T>(this IReadOnlyList<T> list) => list[list.Count - 1];
}
