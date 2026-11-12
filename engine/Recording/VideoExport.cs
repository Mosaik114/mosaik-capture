namespace MosaikCapture.Engine.Recording;

/// <summary>Trimming a recording into a new MP4 (re-encoded, frame accurate) or a GIF.</summary>
internal static class VideoExport
{
    const string GifScale = "fps=15,scale='min(960,iw)':-2:flags=lanczos";
    static readonly TimeSpan EncoderTimeout = TimeSpan.FromSeconds(10);

    // Share of the GIF progress bar for the palette pass.
    const double PaletteStarted = 0.03, PaletteDone = 0.15;
    static readonly string[] KeepAudio = { "-map", "0:a:0", "-c:a", "aac", "-b:a", "192k" }, NoAudio = { "-an" };

    /// <summary>
    /// Exports [<paramref name="from"/>, <paramref name="to"/>] next to the source. Call on the UI thread;
    /// <paramref name="progress"/> (0..1, never decreasing) and <paramref name="done"/> (result or error
    /// message) are invoked on it as well. After <paramref name="cancellation"/> fired, ffmpeg is killed,
    /// the partial file removed and <paramref name="done"/> is not called.
    /// </summary>
    public static void Export(RecordedVideo source, double from, double to, bool sound, bool gif,
        Action<double> progress, Action<RecordedVideo?, string?> done, CancellationToken cancellation = default)
    {
        string target = ExportTarget(source.Path, gif);
        double length = Math.Max(0.1, to - from);
        var dispatcher = Application.Current.Dispatcher;
        var reporter = new MonotonicProgress(p => dispatcher.BeginInvoke(new Action(() => { if (!cancellation.IsCancellationRequested) progress(p); })));
        Task.Run(() =>
        {
            try
            {
                return gif ? ExportGif(source, from, to, length, target, reporter, cancellation) : ExportMp4(source, from, to, sound, length, target, reporter, cancellation);
            }
            catch
            {
                DeletePartial(target);
                throw;
            }
        }, cancellation)
            .ContinueWith(task =>
            {
                if (task.IsCanceled || cancellation.IsCancellationRequested) return;
                if (task.IsFaulted)
                {
                    ErrorLog.Write("Exporting a recording", task.Exception);
                    done(null, task.Exception!.InnerException!.Message);
                }
                else done(task.Result, null);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Target file next to the source: its name plus <see cref="UiText.Files.TrimmedSuffix"/> as MP4, or its
    /// name as GIF; numbered instead of overwriting an earlier export.
    /// </summary>
    internal static string ExportTarget(string source, bool gif) =>
        UniqueFile.Next(Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + (gif ? ".gif" : UiText.Files.TrimmedSuffix + ".mp4")));

    /// <summary>ffmpeg input arguments that seek to [from, to] of the source.</summary>
    internal static string[] CutInput(string source, double from, double to) =>
        new[] { "-ss", FfmpegArgs.Number(from), "-to", FfmpegArgs.Number(to), "-i", source };

    internal static FfmpegArgs Mp4Arguments(string source, double from, double to, bool keepAudio, VideoEncoder encoder, string target) =>
        FfmpegArgs.Quiet()
            .Add(CutInput(source, from, to))
            .Add("-map", "0:v:0")
            .Add(keepAudio ? KeepAudio : NoAudio)
            .Add(Ffmpeg.FileVideoArgs(encoder))
            .Add("-movflags", "+faststart", "-progress", "pipe:1", "-nostats", target);

    // GIFs take two passes: first a palette for exactly these frames, then the GIF using it.
    internal static FfmpegArgs GifPaletteArguments(string source, double from, double to, string palette) =>
        FfmpegArgs.Quiet().Add(CutInput(source, from, to)).Add("-vf", GifScale + ",palettegen=stats_mode=diff", palette);

    internal static FfmpegArgs GifArguments(string source, double from, double to, string palette, string target) =>
        FfmpegArgs.Quiet()
            .Add(CutInput(source, from, to))
            .Add("-i", palette, "-lavfi", GifScale + "[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle")
            .Add("-loop", "0", "-progress", "pipe:1", "-nostats", target);

    static RecordedVideo ExportMp4(RecordedVideo source, double from, double to, bool sound, double length, string target,
        MonotonicProgress progress, CancellationToken cancellation)
    {
        var encoder = Ffmpeg.EncoderOrSoftwareAsync(EncoderTimeout).GetAwaiter().GetResult();   // background thread
        var args = Mp4Arguments(source.Path, from, to, sound && source.Audio, encoder, target);
        int code = Ffmpeg.Run(Ffmpeg.Exe, args, FfmpegTimeouts.ForMedia(length), out _, out string error, seconds => progress.Report(seconds / length), cancellation);
        return Finish(code, error, target);
    }

    static RecordedVideo ExportGif(RecordedVideo source, double from, double to, double length, string target,
        MonotonicProgress progress, CancellationToken cancellation)
    {
        string palette = Path.Combine(Path.GetTempPath(), "mosaik-palette-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            progress.Report(PaletteStarted);
            int code = Ffmpeg.Run(Ffmpeg.Exe, GifPaletteArguments(source.Path, from, to, palette), FfmpegTimeouts.ForMedia(length), out _, out string error,
                cancellation: cancellation);
            if (code == 0)
            {
                progress.Report(PaletteDone);
                code = Ffmpeg.Run(Ffmpeg.Exe, GifArguments(source.Path, from, to, palette, target), FfmpegTimeouts.ForMedia(length), out _, out error,
                    seconds => progress.Report(PaletteDone + (1 - PaletteDone) * seconds / length), cancellation);
            }
            return Finish(code, error, target);
        }
        finally
        {
            try { File.Delete(palette); }
            catch (IOException ex) { ErrorLog.Write("Removing the GIF palette", ex); }
        }
    }

    static RecordedVideo Finish(int exitCode, string error, string target)
    {
        if (exitCode != 0) throw new InvalidOperationException(FfmpegOutput.LastLine(error));
        return RecordedVideo.Load(target);
    }

    static void DeletePartial(string target)
    {
        try { File.Delete(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("Removing a partial export", ex); }   // still locked
    }
}

/// <summary>
/// Progress that is clamped to 0..1 and never goes backwards (ffmpeg sometimes briefly reports an
/// earlier time on slow machines).
/// </summary>
internal sealed class MonotonicProgress
{
    readonly Action<double> report;
    double shown;

    public MonotonicProgress(Action<double> report) => this.report = report;

    public void Report(double value)
    {
        value = Math.Max(0, Math.Min(1, value));
        if (value < shown) return;
        shown = value;
        report(value);
    }
}

/// <summary>File names that never overwrite an existing file.</summary>
internal static class UniqueFile
{
    /// <summary><paramref name="path"/> if free, otherwise "name (2).ext", "name (3).ext", ...</summary>
    public static string Next(string path)
    {
        string directory = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(directory, stem + " (" + i + ")" + extension);
        return path;
    }
}
