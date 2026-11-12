namespace MosaikCapture.Engine.Recording;

/// <summary>How an ffmpeg or ffprobe call ended.</summary>
internal readonly struct FfmpegResult
{
    public FfmpegResult(int exitCode, string output, string error)
    {
        ExitCode = exitCode;
        Output = output;
        Error = error;
    }

    public int ExitCode { get; }
    /// <summary>Standard output (the end of it).</summary>
    public string Output { get; }
    /// <summary>The end of the error output, where ffmpeg puts the actual error.</summary>
    public string Error { get; }

    public bool Succeeded => ExitCode == 0;
}
