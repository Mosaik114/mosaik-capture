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

    RecordingSession NewSession() => new(Area, 0, Desktop, new AudioSettings(false, true, false), new RecordingSessionServices
    {
        Runner = runner,
        CreateAudio = (_, _) => audio,
        WorkRoot = root.Path,
        Fps = () => 30,
        Encoder = () => encoder(),
        NudgeFirstFrame = (x, y) => { lock (nudges) nudges.Add(new Point(x, y)); },
        LoadVideo = path => new RecordedVideo { Path = path, Duration = 4 },
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
        runner.StartGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession();

        var resume = session.ResumeAsync();
        await Within(runner.StartEntered.Task);   // ffmpeg is being started on a pool thread, outside the lock
        session.Pause();
        runner.StartGate.SetResult(true);
        await Within(resume);

        await Within(runner.Processes.Single().Quit.Task);
        Assert.Empty(nudges);
        Assert.True(session.Paused);
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
        session.Broke += () => lockFree = Task.Run(() => session.SegmentCount).Wait(TimeSpan.FromSeconds(2));

        await Within(session.ResumeAsync());

        Assert.True(lockFree, "Broke was raised while the session lock was held");
        Assert.Equal("ffmpeg.exe is missing", session.Reason);
        Assert.Equal(0, session.SegmentCount);
        Assert.True(session.Paused);
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

        var video = await Task.Run(() => session.Finish(target));

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

        var video = await Task.Run(() => session.Finish(root.File("out.mp4")));

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

        var video = await Task.Run(() => session.Finish(target));

        Assert.Null(video);
        Assert.Equal("Conversion failed!", session.Reason);
        Assert.False(File.Exists(target));
        Assert.True(Directory.Exists(session.WorkDirectory));
    }

    [Fact]
    public async Task UnfinishedAudioIsLeftOutAndReported()
    {
        audio.StopResult = false;
        var session = NewSession();
        await Within(session.ResumeAsync());

        var video = await Task.Run(() => session.Finish(root.File("out.mp4")));

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
        for (int i = 0; i < 100 && Directory.Exists(session.WorkDirectory); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
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
        public TaskCompletionSource<bool>? StartGate;
        public Exception? StartFailure;
        public double ProbedDuration = 2;
        public int AssembleExitCode;
        public string[] ConcatLines = Array.Empty<string>();

        public IFfmpegProcess Start(FfmpegArgs args, Action<string> errorLine, Action exited)
        {
            StartEntered.TrySetResult(true);
            StartGate?.Task.Wait(TimeSpan.FromSeconds(10));
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

        public int Run(FfmpegProgram program, FfmpegArgs args, TimeSpan timeout, out string output, out string error)
        {
            lock (Runs) Runs.Add((program, args.Arguments));
            error = "";
            if (program == FfmpegProgram.Probe)
            {
                output = "start_time=0.1\nduration=" + FfmpegArgs.Number(ProbedDuration) + "\n";
                return 0;
            }
            output = "";
            ConcatLines = File.ReadAllLines(args.Arguments[args.Arguments.ToList().IndexOf("-i") + 1]);
            if (AssembleExitCode != 0)
            {
                File.WriteAllText(args.Arguments.LastItem(), "partial");
                error = "Conversion failed!";
                return AssembleExitCode;
            }
            File.WriteAllText(args.Arguments.LastItem(), "video");
            return 0;
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
