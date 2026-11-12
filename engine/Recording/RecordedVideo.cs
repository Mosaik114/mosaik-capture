namespace MosaikCapture.Engine.Recording;

/// <summary>A finished video or GIF with its key data and a thumbnail.</summary>
internal sealed class RecordedVideo
{
    public string Path { get; init; } = "";
    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Audio { get; init; }
    public bool Gif { get; init; }
    public long Bytes { get; init; }
    public BitmapSource? Thumbnail { get; init; }

    /// <summary>Reads the key data with ffprobe and takes a thumbnail from the middle (blocking).</summary>
    /// <exception cref="InvalidOperationException">ffprobe cannot read the file (message: its last error line).</exception>
    /// <exception cref="TimeoutException">ffprobe did not answer in time.</exception>
    public static RecordedVideo Load(string path)
    {
        var probe = new FfmpegArgs().Add("-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "default=nw=1", path);
        if (Ffmpeg.Run(Ffmpeg.Probe, probe, FfmpegTimeouts.Quick, out string output, out string error) != 0)
            throw new InvalidOperationException(FfmpegOutput.LastLine(error));
        double duration = FfmpegOutput.Field(output, "duration");
        return new RecordedVideo
        {
            Path = path,
            Gif = string.Equals(System.IO.Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase),
            Duration = duration,
            Width = (int)FfmpegOutput.Field(output, "width"),
            Height = (int)FfmpegOutput.Field(output, "height"),
            Audio = output.Contains("codec_type=audio"),
            Bytes = new FileInfo(path).Length,
            Thumbnail = Frame(path, duration / 2, 960)
        };
    }

    /// <summary>A single frame from the video, at most <paramref name="maxWidth"/> pixels wide (blocking).</summary>
    public static BitmapSource? Frame(string path, double at, int maxWidth) =>
        RenderToImage("mosaik-frame-", png => FrameArguments(path, at, maxWidth, png));

    /// <summary>
    /// Film strip for trimming: <paramref name="count"/> frames side by side, <paramref name="height"/>
    /// pixels high (fast: every frame uses its own seek). Blocking.
    /// </summary>
    public static BitmapSource? Strip(string path, double duration, int count, int height) =>
        RenderToImage("mosaik-strip-", png => StripArguments(path, duration, count, height, png));

    internal static FfmpegArgs FrameArguments(string path, double at, int maxWidth, string png) =>
        FfmpegArgs.Quiet().Add("-ss", FfmpegArgs.Number(Math.Max(0, at)), "-i", path, "-frames:v", "1", "-vf", "scale='min(" + maxWidth + ",iw)':-2", png);

    internal static FfmpegArgs StripArguments(string path, double duration, int count, int height, string png)
    {
        var args = FfmpegArgs.Quiet();
        var filter = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            args.Add("-ss", FfmpegArgs.Number(duration * (i + 0.5) / count), "-i", path);
            filter.Append("[" + i + ":v]scale=-2:" + height + ",setsar=1[v" + i + "];");
        }
        for (int i = 0; i < count; i++) filter.Append("[v" + i + "]");
        filter.Append(count > 1 ? "hstack=inputs=" + count : "null");
        return args.Add("-filter_complex", filter.ToString(), "-frames:v", "1", png);
    }

    static BitmapSource? RenderToImage(string prefix, Func<string, FfmpegArgs> arguments)
    {
        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            if (Ffmpeg.Run(Ffmpeg.Exe, arguments(png), FfmpegTimeouts.Quick, out _, out _) != 0 || !File.Exists(png)) return null;
            return ToolWindows.LoadImage(png);
        }
        catch (TimeoutException ex)
        {
            ErrorLog.Write("Rendering a preview image", ex);
            return null;
        }
        finally
        {
            try { File.Delete(png); }
            catch (IOException ex) { ErrorLog.Write("Removing a temporary image", ex); }
        }
    }
}
