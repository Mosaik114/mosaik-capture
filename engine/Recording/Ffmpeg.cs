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

/// <summary>
/// Locates and launches ffmpeg/ffprobe (hidden, tied to the engine's lifetime) and detects the
/// H.264 encoder once per process.
/// </summary>
internal static class Ffmpeg
{
    public static readonly string? Exe = Find("ffmpeg.exe"), Probe = Find("ffprobe.exe");

    static readonly Lazy<Task<VideoEncoder>> encoderDetection = new(() => Task.Run(DetectEncoder), LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly object jobGate = new();
    static IntPtr job;

    public static bool Available => Exe != null && Probe != null;

    /// <summary>Starts the encoder detection (once) and returns its result; never faults.</summary>
    public static Task<VideoEncoder> DetectEncoderAsync() => encoderDetection.Value;

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
        var probe = new FfmpegArgs().Add("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2",
            "-frames:v", "2", "-c:v", "h264_nvenc", "-f", "null", "-");
        try { return Run(Exe, probe, null, out _, out _) == 0 ? VideoEncoder.Nvenc : VideoEncoder.Software; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            ErrorLog.Write("Detecting the video encoder", ex);
            return VideoEncoder.Software;
        }
    }

    // ---------- Locating ----------

    // ffmpeg sees every recording, so well-known install locations come first (winget, Program Files,
    // Chocolatey, Scoop) and only fully qualified local PATH entries are used: a relative entry such as "."
    // or a network share could otherwise inject a foreign ffmpeg.exe.
    static string? Find(string name) => Find(name, SearchLocations());

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
        if (exited != null) process.Exited += (_, _) => exited();
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

    /// <summary>
    /// Runs to completion (call from a background thread). <paramref name="progress"/> receives the
    /// seconds reported by <c>-progress pipe:1</c>.
    /// </summary>
    public static int Run(string? exe, FfmpegArgs args, Action<double>? progress, out string output, out string error)
    {
        var outputText = new StringBuilder();
        var errorText = new StringBuilder();
        using (var process = Start(exe, args, line =>
        {
            lock (outputText) outputText.AppendLine(line);
            if (progress != null && FfmpegOutput.ProgressSeconds(line) is double seconds) progress(seconds);
        }, line => { lock (errorText) if (errorText.Length < 8000) errorText.AppendLine(line); }))
        {
            // Without a timeout this also waits until the redirected output has been read to the end.
            process.WaitForExit();
            lock (outputText) output = outputText.ToString();
            lock (errorText) error = errorText.ToString().Trim();
            return process.ExitCode;
        }
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
