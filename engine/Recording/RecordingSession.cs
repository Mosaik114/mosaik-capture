using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// The recording itself: every stretch between two pauses is a video segment recorded by ffmpeg
/// (Desktop Duplication); audio runs continuously in <see cref="AudioRecorder"/>. At the end the
/// segments are joined without re-encoding and exactly the matching pieces of audio laid underneath.
/// </summary>
/// <remarks>
/// Threading contract:
/// <list type="bullet">
/// <item><see cref="ResumeAsync"/>, <see cref="Pause"/>, <see cref="SetMicrophone"/> and <see cref="SetSystemSound"/>
/// come from the UI thread; the continuation of <see cref="ResumeAsync"/> may run on a pool thread.</item>
/// <item><see cref="Elapsed"/>, <see cref="Paused"/> and <see cref="MicrophoneLevel"/> may be read from any thread.</item>
/// <item><see cref="Finish"/> or <see cref="Discard"/> runs once, on a background thread, after the UI stopped
/// calling the methods above.</item>
/// <item><see cref="Broke"/> is raised on the thread that noticed ffmpeg's exit (a pool thread).</item>
/// <item>The segment list and the pause counter change only under <c>gate</c>; readers take a snapshot under it.
/// <c>current</c> is volatile, the recorded time and the resume flag use <see cref="Interlocked"/>.</item>
/// </list>
/// </remarks>
internal sealed class RecordingSession : IRecordingSession
{
    static readonly TimeSpan PreviousSegmentTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan EncoderTimeout = TimeSpan.FromSeconds(4);

    /// <summary>"q" lets ffmpeg finalize the file; after this it is killed.</summary>
    static readonly TimeSpan GracefulQuitTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Longest wait for all segments to stop: graceful quit plus kill.</summary>
    static readonly TimeSpan SegmentStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Discarding is not worth waiting long for: the work folder is removed with the next recording otherwise.</summary>
    static readonly TimeSpan DiscardStopTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Segments shorter than this hold no usable frame.</summary>
    const double MinimumSegmentSeconds = 0.05;

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
        public Process? Process { get; set; }
        /// <summary>Completes once ffmpeg has exited after a pause.</summary>
        public Task? Stopped { get; set; }
        /// <summary>First frame relative to <see cref="StartEpochMicros"/>, in seconds (ffprobe).</summary>
        public double ProbedStart { get; set; }
        /// <summary>Length in seconds (ffprobe).</summary>
        public double ProbedDuration { get; set; }
        public StringBuilder Errors { get; } = new();
    }

    readonly AudioRecorder audio;
    readonly int output, offsetX, offsetY;
    readonly List<Segment> segments = new();
    readonly object gate = new();
    volatile Segment? current;       // the running segment, null while paused
    VideoEncoder? encoder;           // chosen once, so all segments can be joined without re-encoding (UI thread / resume only)
    long recordedTicks;              // length of the finished segments (Stopwatch ticks); Interlocked
    int pauseCount;                  // under gate: a resume that started before the latest pause gives up
    int resuming;                    // 1 while a resume waits for ffmpeg; Interlocked

    public RecordingSession(Int32Rect area, int output, RECT desktop, AudioSettings audioSettings)
    {
        Area = area;
        this.output = output;
        offsetX = area.X - desktop.left;
        offsetY = area.Y - desktop.top;
        WorkDirectory = Path.Combine(CaptureSettings.RecordingWorkRoot, Begun.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(WorkDirectory);
        audio = new AudioRecorder(WorkDirectory, audioSettings.Microphone, audioSettings.SystemSound);
    }

    public Int32Rect Area { get; }
    public DateTime Begun { get; } = DateTime.Now;
    public string WorkDirectory { get; }
    public string Reason { get; private set; } = UiText.Recording.NoFrames;
    public bool AudioLost { get; private set; }
    public event Action? Broke;

    public bool Paused => current == null && Volatile.Read(ref resuming) == 0;
    public float MicrophoneLevel => audio.MicrophoneLevel;

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

    public async Task ResumeAsync()
    {
        if (current != null || Interlocked.CompareExchange(ref resuming, 1, 0) != 0) return;
        int pausesBefore;
        Task? previous;
        lock (gate)
        {
            pausesBefore = pauseCount;
            previous = segments.Count > 0 ? segments[segments.Count - 1].Stopped : null;
        }
        try
        {
            // ffmpeg of the previous segment must have released the GPU output
            if (previous != null) await Task.WhenAny(previous, Task.Delay(PreviousSegmentTimeout));
            encoder ??= await Ffmpeg.EncoderOrSoftwareAsync(EncoderTimeout);
            lock (gate)
            {
                if (pausesBefore == pauseCount) StartSegment(encoder.Value);
            }
        }
        finally { Volatile.Write(ref resuming, 0); }
    }

    // Called under the lock.
    void StartSegment(VideoEncoder videoEncoder)
    {
        var segment = new Segment(Path.Combine(WorkDirectory, "segment-" + (segments.Count + 1).ToString("00", CultureInfo.InvariantCulture) + ".mkv"),
            Clocks.EpochMicros(), Clocks.Now100Ns());
        var args = SegmentArguments(output, CaptureSettings.Fps, Area, offsetX, offsetY, segment.StartEpochMicros, videoEncoder, segment.File);
        segment.StopwatchStart = Stopwatch.GetTimestamp();
        segments.Add(segment);
        current = segment;
        try
        {
            segment.Process = Ffmpeg.Start(Ffmpeg.Exe, args, null, line => AppendError(segment, line), () => OnSegmentExited(segment));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Starting ffmpeg", ex);
            segments.Remove(segment);
            current = null;
            Reason = ex.Message;
            Broke?.Invoke();
            return;
        }
        FirstFrameNudge.Poke(Area.X + Area.Width - 2, Area.Y + Area.Height - 2);
    }

    /// <summary>
    /// ffmpeg arguments for one segment: Desktop Duplication of the area, timestamps relative to
    /// <paramref name="startEpochMicros"/> (ffprobe then reports where the first frame lies).
    /// </summary>
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

    static void AppendError(Segment segment, string line)
    {
        lock (segment.Errors) if (segment.Errors.Length < 4000) segment.Errors.AppendLine(line);
    }

    void OnSegmentExited(Segment segment)
    {
        if (current != segment) return;   // stopped on purpose
        lock (segment.Errors) Reason = segment.Errors.Length > 0 ? FfmpegOutput.LastLine(segment.Errors.ToString()) : UiText.Recording.StoppedUnexpectedly;
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
        }
        Interlocked.Add(ref recordedTicks, Stopwatch.GetTimestamp() - segment.StopwatchStart);
        var process = segment.Process!;
        segment.Stopped = Task.Run(() => QuitGracefully(process));
    }

    // "q" stops ffmpeg cleanly (the file is finalized properly); kill it if necessary.
    static void QuitGracefully(Process process)
    {
        try
        {
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }   // ffmpeg already exited
        if (!process.WaitForExit((int)GracefulQuitTimeout.TotalMilliseconds)) Ffmpeg.Kill(process);
        process.Dispose();
    }

    List<Segment> SnapshotSegments()
    {
        lock (gate) return segments.ToList();
    }

    void WaitForSegments(TimeSpan timeout)
    {
        var stopped = SnapshotSegments().Select(s => s.Stopped).OfType<Task>().ToArray();
        if (!Task.WaitAll(stopped, timeout)) ErrorLog.Write("A recording segment did not stop in time", null);
    }

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
                lock (segment.Errors) if (segment.Errors.Length > 0) Reason = FfmpegOutput.LastLine(segment.Errors.ToString());
            return null;
        }

        var tracks = new List<string>();
        if (audioComplete && audio.SystemSoundUsed) tracks.Add(audio.SystemSoundFile);
        if (audioComplete && audio.MicrophoneUsed) tracks.Add(audio.MicrophoneFile);
        var args = Assemble(WorkDirectory, parts.Select(p => p.File).ToList(), tracks,
            parts.Select(p => Math.Max(0, audio.Offset(p.AudioClockStart) + p.ProbedStart)).ToList(), parts.Select(p => p.ProbedDuration).ToList(), path);

        try
        {
            if (Ffmpeg.Run(Ffmpeg.Exe, args, FfmpegTimeouts.ForMedia(parts.Sum(p => p.ProbedDuration)), out _, out string error) != 0 || !File.Exists(path))
                return Failed(FfmpegOutput.LastLine(error), path);
            var video = RecordedVideo.Load(path);
            DeleteWorkDirectory();
            return video;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception)
        {
            ErrorLog.Write("Assembling a recording", ex);
            return Failed(ex.Message, path);
        }
    }

    // The segments stay in the work folder (removed with a later recording); a partial result goes.
    RecordedVideo? Failed(string reason, string path)
    {
        Reason = reason;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("Removing a partial recording", ex); }   // still locked: left behind
        return null;
    }

    void DeleteWorkDirectory()
    {
        try { Directory.Delete(WorkDirectory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Removing the recording work folder (removed on the next recording)", ex);
        }
    }

    /// <summary>
    /// ffmpeg arguments for joining: the segments concatenated without re-encoding, plus from each
    /// audio track exactly the pieces during which video was recorded (from/length per segment, in
    /// seconds on the audio clock). Writes the concat list into <paramref name="directory"/>.
    /// </summary>
    internal static FfmpegArgs Assemble(string directory, IList<string> files, IList<string> tracks, IList<double> from, IList<double> length, string path)
    {
        string list = Path.Combine(directory, "segments.txt");
        File.WriteAllLines(list, files.Select(f => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'"));

        var args = FfmpegArgs.Quiet().Add("-f", "concat", "-safe", "0", "-i", list);
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

    static bool Probe(Segment segment)
    {
        var args = new FfmpegArgs().Add("-v", "error", "-show_entries", "format=start_time,duration", "-of", "default=nw=1", segment.File);
        try
        {
            if (Ffmpeg.Run(Ffmpeg.Probe, args, FfmpegTimeouts.Quick, out string output, out _) != 0) return false;
            segment.ProbedStart = FfmpegOutput.Field(output, "start_time");
            segment.ProbedDuration = FfmpegOutput.Field(output, "duration");
            return segment.ProbedDuration > MinimumSegmentSeconds;
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Probing a recording segment", ex);
            return false;
        }
    }
}
