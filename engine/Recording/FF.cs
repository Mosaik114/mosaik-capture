namespace MosaikCapture.Engine.Recording;

// Locates and launches ffmpeg (hidden, tied to our lifetime) and picks the video encoder.
static class FF
{
    public static readonly string? Exe = Find("ffmpeg.exe"), Probe = Find("ffprobe.exe");
    static string encoder = "";
    static Task? detect;
    static IntPtr job;

    public static bool Nvenc => encoder == "nvenc";

    // While recording: fast, good quality.
    public static string LiveVideo => Nvenc ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 22 -b:v 0 -g 120" : "-c:v libx264 -preset veryfast -crf 20 -g 120";

    // When trimming: slower, more thorough.
    public static string FileVideo => (Nvenc ? "-c:v h264_nvenc -preset p5 -rc vbr -cq 21 -b:v 0" : "-c:v libx264 -preset medium -crf 20") + " -pix_fmt yuv420p";

    // ffmpeg sees every recording, so well-known install locations come first (winget, Program Files,
    // Chocolatey, Scoop) and only fully qualified local PATH entries are used: a relative entry such as "."
    // or a network share could otherwise inject a foreign ffmpeg.exe.
    static string? Find(string name)
    {
        return Find(name, SearchLocations());
    }

    internal static IEnumerable<string> SearchLocations()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) yield return d;
    }

    internal static string? Find(string name, IEnumerable<string?> dirs)
    {
        foreach (string? entry in dirs)
        {
            string d = (entry ?? "").Trim().Trim('"');
            if (!IsLocalPath(d)) continue;
            try
            {
                string p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }   // invalid PATH entry: skip it
        }
        return null;
    }

    // Only "C:\..." counts: not ".\", "folder", "C:folder" (drive-relative) or "\\server\...".
    internal static bool IsLocalPath(string d)
    {
        return d.Length >= 3 && char.IsLetter(d[0]) && d[1] == ':' && (d[2] == '\\' || d[2] == '/');
    }

    // Is the GPU encoder available? Checked once, then cached.
    public static Task Detect()
    {
        if (detect == null)
            detect = Task.Factory.StartNew(delegate
            {
                encoder = Run(Exe, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.2 -frames:v 2 -c:v h264_nvenc -f null -",
                    null, out string o, out string e) == 0 ? "nvenc" : "x264";
            });
        return detect;
    }

    public static Process Start(string? exe, string args, Action<string>? line, Action<string>? err)
    {
        var p = new Process
        {
            EnableRaisingEvents = true,
            StartInfo = new ProcessStartInfo(exe, args)
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
        p.OutputDataReceived += (o, e) => { if (e.Data != null && line != null) line(e.Data); };
        p.ErrorDataReceived += (o, e) => { if (e.Data != null && err != null) err(e.Data); };
        p.Start();
        Adopt(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    // Runs to completion (call from a background thread); progress receives the seconds reported by -progress.
    public static int Run(string? exe, string args, Action<double>? progress, out string output, out string error)
    {
        var o = new StringBuilder();
        var e = new StringBuilder();
        var p = Start(exe, args, l =>
        {
            lock (o) o.AppendLine(l);
            if (progress != null && l.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(l.Substring(12), out long us) && us >= 0) progress(us / 1e6);
        }, l => { lock (e) if (e.Length < 8000) e.AppendLine(l); });
        p.WaitForExit();
        p.WaitForExit();   // without a timeout this also waits until the output has been read completely
        int code = p.ExitCode;
        p.Dispose();
        lock (o) output = o.ToString();
        lock (e) error = e.ToString().Trim();
        return code;
    }

    // Puts the process into a job object: when the engine exits, ffmpeg exits too (no orphaned recordings).
    static void Adopt(Process p)
    {
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                int size = IntPtr.Size == 8 ? 144 : 112;   // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                IntPtr info = Marshal.AllocHGlobal(size);
                for (int i = 0; i < size; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteInt32(info, 16, 0x2000);      // LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(job, 9, info, (uint)size);
                Marshal.FreeHGlobal(info);
            }
            AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception ex) { ErrorLog.Write("ffmpeg an das Job-Objekt binden", ex); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr sa, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
