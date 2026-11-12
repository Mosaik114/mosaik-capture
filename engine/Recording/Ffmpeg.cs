using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>Which H.264 encoder ffmpeg uses.</summary>
internal enum VideoEncoder
{
    /// <summary>libx264 on the CPU.</summary>
    Software,
    /// <summary>h264_nvenc on an NVIDIA GPU.</summary>
    Nvenc,
}

/// <summary>How long a single ffmpeg/ffprobe call may take before it is killed.</summary>
internal static class FfmpegTimeouts
{
    /// <summary>ffprobe, encoder detection, a single frame or the film strip.</summary>
    public static readonly TimeSpan Quick = TimeSpan.FromMinutes(1);

    /// <summary>Waiting for a killed process to be gone.</summary>
    public static readonly TimeSpan Kill = TimeSpan.FromSeconds(5);

    /// <summary>Fixed part of every encoding call.</summary>
    static readonly TimeSpan EncodingBase = TimeSpan.FromMinutes(2);

    /// <summary>Slowest plausible encoding speed relative to real time.</summary>
    const double SlowestSpeed = 0.1;

    /// <summary>Joining or re-encoding <paramref name="mediaSeconds"/> of video.</summary>
    public static TimeSpan ForMedia(double mediaSeconds) => EncodingBase + TimeSpan.FromSeconds(Math.Max(0, mediaSeconds) / SlowestSpeed);
}

/// <summary>
/// Locates and launches ffmpeg/ffprobe (hidden, tied to the engine's lifetime) and detects the
/// H.264 encoder once per process.
/// </summary>
/// <remarks>Thread-safe: all members may be called from any thread.</remarks>
internal static class Ffmpeg
{
    const int WaitSliceMs = 100;

    /// <summary>Characters kept from the end of ffmpeg's error output (the actual error is printed last).</summary>
    const int ErrorTail = 8000;

    /// <summary>Kept from the standard output of calls without progress (ffprobe answers are a few lines).</summary>
    const int OutputTail = 64_000;

    /// <summary>A detection that failed (not: found no NVENC) is tried again after this.</summary>
    static readonly TimeSpan DetectionRetry = TimeSpan.FromMinutes(5);
    static readonly object detectionGate = new();
    static Task<VideoEncoder>? detection;
    static DateTime detectionFailedAt;
    static volatile bool detectionFailed;
    static readonly object jobGate = new();
    static IntPtr job;
    /// <summary>Where ffmpeg and ffprobe are; tests can point it elsewhere.</summary>
    internal static IFfmpegLocator Locator { get; set; } = new FfmpegLocator(SearchLocations);

    /// <summary>ffmpeg.exe, or null (see <see cref="FfmpegLocator"/>).</summary>
    public static string? Exe => Locator.Ffmpeg;

    /// <summary>ffprobe.exe, preferably the one next to <see cref="Exe"/> (same install), or null.</summary>
    public static string? Probe => Locator.Probe;

    public static bool Available => Exe != null && Probe != null;

    /// <summary>
    /// Starts the encoder detection (once ffmpeg exists) and returns its result; never faults. The result is
    /// kept, except after a failed detection (timeout, ffmpeg not startable), which is retried after a while.
    /// </summary>
    public static Task<VideoEncoder> DetectEncoderAsync()
    {
        if (Exe == null) return Task.FromResult(VideoEncoder.Software);
        lock (detectionGate)
        {
            bool retry = detection != null && detection.IsCompleted && detectionFailed && DateTime.UtcNow - detectionFailedAt > DetectionRetry;
            if (detection == null || retry)
            {
                detectionFailed = false;
                detection = Task.Run(DetectEncoder);
            }
            return detection;
        }
    }

    /// <summary>The detected encoder, waiting up to <paramref name="timeout"/>; software if unknown by then.</summary>
    public static async Task<VideoEncoder> EncoderOrSoftwareAsync(TimeSpan timeout)
    {
        var detection = DetectEncoderAsync();
        return await Task.WhenAny(detection, Task.Delay(timeout)).ConfigureAwait(false) == detection && detection.Status == TaskStatus.RanToCompletion
            ? detection.Result
            : VideoEncoder.Software;
    }

    /// <summary>While recording: fast, good quality.</summary>
    public static string[] LiveVideoArgs(VideoEncoder encoder) => encoder == VideoEncoder.Nvenc
        ? new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "22", "-b:v", "0", "-g", "120" }
        : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "120" };

    /// <summary>When trimming: slower, more thorough.</summary>
    public static string[] FileVideoArgs(VideoEncoder encoder) => encoder == VideoEncoder.Nvenc
        ? new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "21", "-b:v", "0", "-pix_fmt", "yuv420p" }
        : new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p" };

    static VideoEncoder DetectEncoder()
    {
        if (Exe == null) return VideoEncoder.Software;
        var test = new FfmpegArgs().Add("-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2",
            "-frames:v", "2", "-c:v", "h264_nvenc", "-f", "null", "-");
        try { return Run(Exe, test, FfmpegTimeouts.Quick, out _, out _) == 0 ? VideoEncoder.Nvenc : VideoEncoder.Software; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            ErrorLog.Write("Detecting the video encoder", ex);
            lock (detectionGate)
            {
                detectionFailed = true;
                detectionFailedAt = DateTime.UtcNow;
            }
            return VideoEncoder.Software;
        }
    }

    // ---------- Locating ----------

    // ffmpeg sees every recording, so well-known install locations come first (winget, Program Files,
    // Chocolatey, Scoop) and only fully qualified local PATH entries are used: a relative entry such as "."
    // or a network share could otherwise inject a foreign ffmpeg.exe.
    /// <summary>ffprobe next to the found ffmpeg first, so both come from the same install; otherwise the usual search.</summary>
    internal static string? FindProbe(string? ffmpeg) => FindProbe(ffmpeg, SearchLocations());

    internal static string? FindProbe(string? ffmpeg, IEnumerable<string?> locations)
    {
        string? directory = ffmpeg == null ? null : Path.GetDirectoryName(ffmpeg);
        return (directory == null ? null : Find("ffprobe.exe", new[] { directory })) ?? Find("ffprobe.exe", locations);
    }

    internal static IEnumerable<string> SearchLocations()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) yield return directory;
    }

    internal static string? Find(string name, IEnumerable<string?> directories)
    {
        foreach (string? entry in directories)
        {
            string directory = (entry ?? "").Trim().Trim('"');
            if (!IsLocalPath(directory)) continue;
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }   // invalid PATH entry: skip it
        }
        return null;
    }

    /// <summary>Only "C:\..." counts: not ".\", "folder", "C:folder" (drive-relative) or "\\server\...".</summary>
    internal static bool IsLocalPath(string directory) =>
        directory.Length >= 3 && char.IsLetter(directory[0]) && directory[1] == ':' && (directory[2] == '\\' || directory[2] == '/');

    // ---------- Running ----------

    /// <summary>
    /// Starts a hidden process with redirected output. <paramref name="exited"/> is subscribed before
    /// the process starts, so even an immediate exit is reported.
    /// </summary>
    /// <remarks>
    /// The process joins the kill-on-close job right after it started. <see cref="Process"/> cannot start
    /// it suspended, so for a moment an engine crash could leave it running; every call we make still
    /// ends on its own (finite input, or the recording's stdin closes with the engine).
    /// </remarks>
    public static Process Start(string? exe, FfmpegArgs args, Action<string>? outputLine, Action<string>? errorLine, Action? exited = null)
    {
        var process = new Process
        {
            EnableRaisingEvents = exited != null,
            StartInfo = new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorLine?.Invoke(e.Data); };
        if (exited != null) process.Exited += (_, _) => ReportExit(process, exited);
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        AddToKillOnCloseJob(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    // On .NET Framework Exited can fire before the last redirected lines arrived: the parameterless
    // WaitForExit waits for the end of both streams, so the callback sees ffmpeg's final error line.
    static void ReportExit(Process process, Action exited)
    {
        try { process.WaitForExit(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception) { }   // already disposed by a stop
        exited();
    }

    /// <summary>
    /// Runs to completion (call from a background thread) and returns the exit code. <paramref name="progress"/>
    /// receives the seconds reported by <c>-progress pipe:1</c>.
    /// </summary>
    /// <exception cref="TimeoutException">The call took longer than <paramref name="timeout"/>; the process was killed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> fired; the process was killed.</exception>
    public static int Run(string? exe, FfmpegArgs args, TimeSpan timeout, out string output, out string error,
        Action<double>? progress = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // with -progress ffmpeg prints a status block every half second: only the progress is used then
        var outputText = new TailBuffer(progress == null ? OutputTail : ErrorTail);
        var errorText = new TailBuffer(ErrorTail);
        using var process = Start(exe, args, line =>
        {
            outputText.Add(line);
            if (progress != null && FfmpegOutput.ProgressSeconds(line) is double seconds) progress(seconds);
        }, errorText.Add);
        CloseInput(process);   // nothing is ever sent: a call that reads stdin ends instead of waiting
        var waited = Stopwatch.StartNew();
        while (!process.WaitForExit(WaitSliceMs))
        {
            if (cancellation.IsCancellationRequested)
            {
                Kill(process);
                throw new OperationCanceledException(cancellation);
            }
            if (waited.Elapsed > timeout)
            {
                Kill(process);
                throw new TimeoutException(UiText.Common.FfmpegTimedOut);
            }
        }
        process.WaitForExit();   // has exited: this only waits for the last redirected output lines
        output = outputText.ToString();
        error = errorText.ToString().Trim();
        return process.ExitCode;
    }

    static void CloseInput(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }   // exited already
    }

    /// <summary>Kills the process and waits (bounded) until it is gone.</summary>
    internal static void Kill(Process process)
    {
        try { process.Kill(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }   // exited in the meantime
        if (!process.WaitForExit((int)FfmpegTimeouts.Kill.TotalMilliseconds))
            ErrorLog.Write("ffmpeg did not end after being killed", null);
    }

    // Puts the process into a job object that kills it when the engine exits (no orphaned recordings).
    static void AddToKillOnCloseJob(Process process)
    {
        lock (jobGate)
        {
            if (job == IntPtr.Zero && !TryCreateJob(out job)) return;
        }
        if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
            ErrorLog.Write("Adding ffmpeg to the job object", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    static bool TryCreateJob(out IntPtr created)
    {
        created = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (created == IntPtr.Zero)
        {
            ErrorLog.Write("Creating the ffmpeg job object", new Win32Exception(Marshal.GetLastWin32Error()));
            return false;
        }
        var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (NativeMethods.SetInformationJobObject(created, NativeMethods.JobObjectExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            return true;
        ErrorLog.Write("Configuring the ffmpeg job object", new Win32Exception(Marshal.GetLastWin32Error()));
        NativeMethods.CloseHandle(created);
        created = IntPtr.Zero;
        return false;
    }
}
