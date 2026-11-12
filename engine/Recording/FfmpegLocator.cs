namespace MosaikCapture.Engine.Recording;

/// <summary>Where ffmpeg and ffprobe are installed.</summary>
internal interface IFfmpegLocator
{
    /// <summary>Full path of ffmpeg.exe, or null if it is not installed.</summary>
    string? Ffmpeg { get; }
    /// <summary>Full path of ffprobe.exe, or null.</summary>
    string? Probe { get; }
}

/// <summary>
/// Finds ffmpeg and ffprobe in the given directories (see <see cref="Ffmpeg.SearchLocations"/>) and
/// remembers them. A remembered file is checked on every use: if it was uninstalled or moved, the search
/// runs again; while nothing is found it runs on every use, so installing ffmpeg needs no restart.
/// Thread-safe (a race only searches twice).
/// </summary>
internal sealed class FfmpegLocator : IFfmpegLocator
{
    readonly Func<IEnumerable<string?>> locations;
    volatile string? ffmpeg, probe;

    public FfmpegLocator(Func<IEnumerable<string?>> locations) => this.locations = locations;

    public string? Ffmpeg
    {
        get
        {
            string? known = ffmpeg;
            if (known != null && File.Exists(known)) return known;
            return ffmpeg = Recording.Ffmpeg.Find("ffmpeg.exe", locations());
        }
    }

    public string? Probe
    {
        get
        {
            string? known = probe;
            if (known != null && File.Exists(known)) return known;
            return probe = Recording.Ffmpeg.FindProbe(Ffmpeg, locations());
        }
    }
}
