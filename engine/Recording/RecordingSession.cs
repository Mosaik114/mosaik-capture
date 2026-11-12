using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>Everything a <see cref="RecordingSession"/> needs from outside; tests replace parts of it.</summary>
internal sealed class RecordingSessionServices
{
    static readonly TimeSpan EncoderTimeout = TimeSpan.FromSeconds(4);

    public static readonly RecordingSessionServices Real = new();

    public IFfmpegRunner Runner { get; init; } = FfmpegRunner.Instance;
    public Func<string, AudioSettings, IAudioRecorder> CreateAudio { get; init; } = (directory, audio) => new AudioRecorder(directory, audio.Microphone, audio.SystemSound);
    public string WorkRoot { get; init; } = CaptureSettings.RecordingWorkRoot;
    public Func<int> Fps { get; init; } = () => CaptureSettings.Fps;
    /// <summary>The encoder for all segments, waited for at most a few seconds.</summary>
    public Func<Task<VideoEncoder>> Encoder { get; init; } = () => Ffmpeg.EncoderOrSoftwareAsync(EncoderTimeout);
    /// <summary>Makes the screen change once so ffmpeg's Desktop Duplication delivers the first frame at once.</summary>
    public Action<int, int> NudgeFirstFrame { get; init; } = FirstFrameNudge.Poke;
    public Func<string, RecordedVideo> LoadVideo { get; init; } = RecordedVideo.Load;
    /// <summary>Time to wait for the ffmpeg of the previous segment before the next one grabs the GPU output.</summary>
    public TimeSpan PreviousSegmentTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The recording itself: every stretch between two pauses is a video segment recorded by ffmpeg
/// (Desktop Duplication); audio runs continuously in an <see cref="IAudioRecorder"/>. At the end the
/// segments are joined without re-encoding and exactly the matching pieces of audio laid underneath.
/// </summary>
/// <remarks>
/// Threading contract:
/// <list type="bullet">
/// <item><see cref="ResumeAsync"/>, <see cref="Pause"/>, <see cref="SetMicrophone"/> and <see cref="SetSystemSound"/>
/// come from the UI thread; <see cref="ResumeAsync"/> continues on a pool thread (ffmpeg starts there).</item>
/// <item><see cref="Elapsed"/>, <see cref="Paused"/>, <see cref="Reason"/> and <see cref="MicrophoneLevel"/> may be read from any thread.</item>
/// <item><see cref="Finish"/> or <see cref="Discard"/> runs once, on a background thread, after the UI stopped
/// calling the methods above.</item>
/// <item><see cref="Broke"/> is raised on the thread that noticed ffmpeg's exit, never while <c>gate</c> is held.</item>
/// <item>Segment list, <c>current</c>, the pause counter and each segment's process and stop task change only
/// under <c>gate</c>; decisions are made under it, ffmpeg is started and events are raised outside of it.
/// The recorded time and the resume flag use <see cref="Interlocked"/>.</item>
/// </list>
/// </remarks>
internal sealed class RecordingSession : IRecordingSession
{
    /// <summary>"q" lets ffmpeg finalize the file; after this it is killed.</summary>
    static readonly TimeSpan GracefulQuitTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Longest wait for all segments to stop: graceful quit plus kill.</summary>
    static readonly TimeSpan SegmentStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Discarding is not worth waiting long for: the work folder is removed with the next recording otherwise.</summary>
    static readonly TimeSpan DiscardStopTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Segments shorter than this hold no usable frame.</summary>
    const double MinimumSegmentSeconds = 0.05;

    /// <summary>Kept from each segment's ffmpeg error output (the end matters).</summary>
    const int ErrorTail = 4000;

    internal const string ConcatListName = "segments.txt";

    /// <summary>A piece of video between two pauses.</summary>
    sealed class Segment
    {
        public Segment(string file, long startEpochMicros, long audioClockStart)
        {
            File = file;
            StartEpochMicros = startEpochMicros;
            AudioClockStart = audioClockStart;
        }

        public string File { get; }
        /// <summary>Start time handed to ffmpeg; frame timestamps are relative to it (µs since 1970).</summary>
        public long StartEpochMicros { get; }
        /// <summary>The same moment on the audio clock (100 ns ticks).</summary>
        public long AudioClockStart { get; }
        /// <summary>Stopwatch timestamp of the start, for the elapsed time.</summary>
        public long StopwatchStart { get; set; }
        /// <summary>Under <c>gate</c>: null until ffmpeg was started.</summary>
        public IFfmpegProcess? Process { get; set; }
        /// <summary>Under <c>gate</c>: completes once ffmpeg has exited after a pause.</summary>
        public Task? Stopped { get; set; }
        /// <summary>First frame relative to <see cref="StartEpochMicros"/>, in seconds (ffprobe).</summary>
        public double ProbedStart { get; set; }
        /// <summary>Length in seconds (ffprobe).</summary>
        public double ProbedDuration { get; set; }
        public TailBuffer Errors { get; } = new(ErrorTail);
    }

    readonly RecordingSessionServices services;
    readonly IAudioRecorder audio;
    readonly int output, offsetX, offsetY;
    readonly List<Segment> segments = new();
    readonly object gate = new();
    volatile Segment? current;       // under gate: the running segment, null while paused
    volatile string reason = UiText.Recording.NoFrames;
    VideoEncoder? encoder;           // chosen once, so all segments can be joined without re-encoding (one resume at a time)
    long recordedTicks;              // length of the finished segments (Stopwatch ticks); Interlocked
    int pauseCount;                  // under gate: a resume that started before the latest pause gives up
    int resuming;                    // 1 while a resume waits for ffmpeg; Interlocked

    public RecordingSession(Int32Rect area, int output, RECT desktop, AudioSettings audioSettings, RecordingSessionServices? services = null)
    {
        this.services = services ?? RecordingSessionServices.Real;
        Area = area;
        this.output = output;
        offsetX = area.X - desktop.left;
        offsetY = area.Y - desktop.top;
        WorkDirectory = Path.Combine(this.services.WorkRoot, Begun.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(WorkDirectory);
        audio = this.services.CreateAudio(WorkDirectory, audioSettings);
    }

    public Int32Rect Area { get; }
    public DateTime Begun { get; } = DateTime.Now;
    public string WorkDirectory { get; }
    public string Reason => reason;
    public bool AudioLost { get; private set; }
    public event Action? Broke;

    public bool Paused => current == null && Volatile.Read(ref resuming) == 0;
    public float MicrophoneLevel => audio.MicrophoneLevel;

    /// <summary>Number of segments started so far (tests).</summary>
    internal int SegmentCount
    {
        get { lock (gate) return segments.Count; }
    }

    public double Elapsed
    {
        get
        {
            long ticks = Interlocked.Read(ref recordedTicks);
            var segment = current;
            if (segment != null) ticks += Stopwatch.GetTimestamp() - segment.StopwatchStart;
            return ticks / (double)Stopwatch.Frequency;
        }
    }

    public void StartAudio() => audio.Start();

    public void SetMicrophone(bool on) => audio.SetMicrophone(on);

    public void SetSystemSound(bool on) => audio.SetSystemSound(on);

    // ---------- Segments ----------

    public async Task ResumeAsync()
    {
        if (current != null || Interlocked.CompareExchange(ref resuming, 1, 0) != 0) return;
        try
        {
            int pausesBefore;
            Task? previous;
            lock (gate)
            {
                pausesBefore = pauseCount;
                previous = segments.Count > 0 ? segments[segments.Count - 1].Stopped : null;
            }
            // ffmpeg of the previous segment must have released the GPU output; nothing here needs the UI thread
            if (previous != null) await Task.WhenAny(previous, Task.Delay(services.PreviousSegmentTimeout)).ConfigureAwait(false);
            encoder ??= await services.Encoder().ConfigureAwait(false);
            StartSegment(encoder.Value, pausesBefore);
        }
        finally { Volatile.Write(ref resuming, 0); }
    }

    // Decides under the lock, starts ffmpeg and reports outside of it.
    void StartSegment(VideoEncoder videoEncoder, int pausesBefore)
    {
        Segment segment;
        lock (gate)
        {
            if (pausesBefore != pauseCount) return;   // paused again while we waited
            segment = new Segment(Path.Combine(WorkDirectory, "segment-" + (segments.Count + 1).ToString("00", CultureInfo.InvariantCulture) + ".mkv"),
                Clocks.EpochMicros(), Clocks.Now100Ns());
            segment.StopwatchStart = Stopwatch.GetTimestamp();
            segments.Add(segment);
            current = segment;
        }
        var args = SegmentArguments(output, services.Fps(), Area, offsetX, offsetY, segment.StartEpochMicros, videoEncoder, segment.File);
        IFfmpegProcess process;
        try
        {
            process = services.Runner.Start(args, segment.Errors.Add, () => OnSegmentExited(segment));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Starting ffmpeg", ex);
            bool wasCurrent;
            lock (gate)
            {
                segments.Remove(segment);
                wasCurrent = current == segment;
                if (wasCurrent) current = null;
            }
            if (!wasCurrent) return;   // paused meanwhile: nothing broke from the user's point of view
            reason = ex.Message;
            Broke?.Invoke();
            return;
        }
        bool pausedMeanwhile;
        lock (gate)
        {
            segment.Process = process;
            pausedMeanwhile = current != segment;
            if (pausedMeanwhile) segment.Stopped = QuitInBackground(process);   // Pause came before the process existed
        }
        if (!pausedMeanwhile) services.NudgeFirstFrame(Area.X + Area.Width - 2, Area.Y + Area.Height - 2);
    }

    internal static FfmpegArgs SegmentArguments(int output, int fps, Int32Rect area, int offsetX, int offsetY, long startEpochMicros, VideoEncoder encoder, string file)
    {
        string grab = "ddagrab=output_idx=" + output + ":framerate=" + fps + ":video_size=" + area.Width + "x" + area.Height +
            ":offset_x=" + offsetX + ":offset_y=" + offsetY;
        const string ColorTags = "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709";
        string timestamps = "setpts=PTS+(RTCSTART-" + startEpochMicros + ")/(1000000*TB)";
        string filter = encoder == VideoEncoder.Nvenc
            ? grab + "," + ColorTags + "," + timestamps
            : grab + ",hwdownload,format=bgra,scale=out_color_matrix=bt601:out_range=tv,format=yuv420p," + ColorTags + "," + timestamps;
        return FfmpegArgs.Quiet()
            .Add("-f", "lavfi", "-i", filter, "-copyts")
            .Add(Ffmpeg.LiveVideoArgs(encoder))
            .Add("-f", "matroska", file);
    }

    void OnSegmentExited(Segment segment)
    {
        if (current != segment) return;   // stopped on purpose
        reason = segment.Errors.IsEmpty ? UiText.Recording.StoppedUnexpectedly : FfmpegOutput.LastLine(segment.Errors.ToString());
        Broke?.Invoke();
    }

    public void Pause()
    {
        Segment? segment;
        lock (gate)
        {
            pauseCount++;
            segment = current;
            if (segment == null) return;
            current = null;
            if (segment.Process != null) segment.Stopped = QuitInBackground(segment.Process);   // otherwise StartSegment stops it
        }
        Interlocked.Add(ref recordedTicks, Stopwatch.GetTimestamp() - segment.StopwatchStart);
    }

    static Task QuitInBackground(IFfmpegProcess process) => Task.Run(() =>
    {
        process.RequestQuit();
        if (!process.WaitForExit(GracefulQuitTimeout)) process.Kill();
        process.Dispose();
    });

    List<Segment> SnapshotSegments()
    {
        lock (gate) return segments.ToList();
    }

    void WaitForSegments(TimeSpan timeout)
    {
        Task[] stopped;
        lock (gate) stopped = segments.Select(s => s.Stopped).OfType<Task>().ToArray();
        if (!Task.WaitAll(stopped, timeout)) ErrorLog.Write("A recording segment did not stop in time", null);
    }

    // ---------- End ----------

    public void Discard()
    {
        Pause();
        WaitForSegments(DiscardStopTimeout);
        audio.Stop();
        DeleteWorkDirectory();
    }

    public RecordedVideo? Finish(string path)
    {
        Pause();
        WaitForSegments(SegmentStopTimeout);
        bool audioComplete = audio.Stop();
        AudioLost = !audioComplete && (audio.SystemSoundUsed || audio.MicrophoneUsed);

        var all = SnapshotSegments();
        var parts = all.Where(s => File.Exists(s.File) && new FileInfo(s.File).Length > 0 && Probe(s)).ToList();
        if (parts.Count == 0)
        {
            foreach (var segment in all)
                if (!segment.Errors.IsEmpty) reason = FfmpegOutput.LastLine(segment.Errors.ToString());
            return null;
        }

        var tracks = new List<string>();
        if (audioComplete && audio.SystemSoundUsed) tracks.Add(audio.SystemSoundFile);
        if (audioComplete && audio.MicrophoneUsed) tracks.Add(audio.MicrophoneFile);
        try
        {
            string list = Path.Combine(WorkDirectory, ConcatListName);
            File.WriteAllLines(list, ConcatList(parts.Select(p => p.File)));
            var args = Assemble(list, tracks, parts.Select(p => Math.Max(0, audio.Offset(p.AudioClockStart) + p.ProbedStart)).ToList(),
                parts.Select(p => p.ProbedDuration).ToList(), path);
            if (services.Runner.Run(FfmpegProgram.Ffmpeg, args, FfmpegTimeouts.ForMedia(parts.Sum(p => p.ProbedDuration)), out _, out string error) != 0 || !File.Exists(path))
                return Failed(FfmpegOutput.LastLine(error), path);
            var video = services.LoadVideo(path);
            DeleteWorkDirectory();
            return video;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Assembling a recording", ex);
            return Failed(ex.Message, path);
        }
    }

    // The segments stay in the work folder (removed with a later recording); a partial result goes.
    RecordedVideo? Failed(string failure, string path)
    {
        reason = failure;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("Removing a partial recording", ex); }   // still locked: left behind
        return null;
    }

    // While the audio thread still holds its WAV files, the folder is removed once it lets go.
    void DeleteWorkDirectory()
    {
        if (audio.Finished.IsCompleted) DeleteWorkDirectoryNow();
        else audio.Finished.ContinueWith(_ => DeleteWorkDirectoryNow(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    void DeleteWorkDirectoryNow()
    {
        try { Directory.Delete(WorkDirectory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Removing the recording work folder (removed on the next recording)", ex);
        }
    }

    // ---------- Assembling (pure) ----------

    /// <summary>Lines of the ffmpeg concat list for <paramref name="files"/> (forward slashes, quotes escaped).</summary>
    internal static IEnumerable<string> ConcatList(IEnumerable<string> files) =>
        files.Select(f => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'");

    /// <summary>
    /// ffmpeg arguments for joining: the segments listed in <paramref name="concatList"/> concatenated without
    /// re-encoding, plus from each audio track exactly the pieces during which video was recorded
    /// (from/length per segment, in seconds on the audio clock).
    /// </summary>
    internal static FfmpegArgs Assemble(string concatList, IList<string> tracks, IList<double> from, IList<double> length, string path)
    {
        var args = FfmpegArgs.Quiet().Add("-f", "concat", "-safe", "0", "-i", concatList);
        foreach (string track in tracks) args.Add("-i", track);
        if (tracks.Count > 0)
        {
            args.Add("-filter_complex", AudioFilter(tracks.Count, from, length), "-map", "0:v", "-map", "[a]", "-c:a", "aac", "-b:a", "192k");
        }
        else args.Add("-map", "0:v");
        return args.Add("-c:v", "copy", "-movflags", "+faststart", path);
    }

    static string AudioFilter(int trackCount, IList<double> from, IList<double> length)
    {
        var filter = new StringBuilder();
        int segmentCount = from.Count;
        for (int track = 0; track < trackCount; track++)
        {
            filter.Append("[" + (track + 1) + ":a]asplit=" + segmentCount);
            for (int i = 0; i < segmentCount; i++) filter.Append("[s" + track + "_" + i + "]");
            filter.Append(';');
            for (int i = 0; i < segmentCount; i++)
                filter.Append("[s" + track + "_" + i + "]atrim=start=" + FfmpegArgs.Number(from[i]) + ":duration=" + FfmpegArgs.Number(length[i]) +
                    ",asetpts=PTS-STARTPTS,apad=whole_dur=" + FfmpegArgs.Number(length[i]) + "[t" + track + "_" + i + "];");
            for (int i = 0; i < segmentCount; i++) filter.Append("[t" + track + "_" + i + "]");
            filter.Append("concat=n=" + segmentCount + ":v=0:a=1[c" + track + "];");
        }
        filter.Append(trackCount == 2 ? "[c0][c1]amix=inputs=2:normalize=0:duration=longest[a]" : "[c0]anull[a]");
        return filter.ToString();
    }

    bool Probe(Segment segment)
    {
        var args = new FfmpegArgs().Add("-v", "error", "-show_entries", "format=start_time,duration", "-of", "default=nw=1", segment.File);
        try
        {
            if (services.Runner.Run(FfmpegProgram.Probe, args, FfmpegTimeouts.Quick, out string probeOutput, out _) != 0) return false;
            segment.ProbedStart = FfmpegOutput.Field(probeOutput, "start_time");
            segment.ProbedDuration = FfmpegOutput.Field(probeOutput, "duration");
            return segment.ProbedDuration > MinimumSegmentSeconds;
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Probing a recording segment", ex);
            return false;
        }
    }
}
