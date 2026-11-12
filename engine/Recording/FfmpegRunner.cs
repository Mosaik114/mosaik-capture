using System.ComponentModel;

namespace MosaikCapture.Engine.Recording;

/// <summary>Which of the two ffmpeg programs to run.</summary>
internal enum FfmpegProgram
{
    Ffmpeg,
    Probe,
}

/// <summary>A running ffmpeg (one recording segment).</summary>
internal interface IFfmpegProcess : IDisposable
{
    /// <summary>Asks ffmpeg to finish the file and quit ("q" on stdin); ignored if it already exited.</summary>
    void RequestQuit();
    bool WaitForExit(TimeSpan timeout);
    /// <summary>Kills the process and waits (bounded) until it is gone.</summary>
    void Kill();
}

/// <summary>Starting and running ffmpeg/ffprobe; a seam so the recording session can be tested without them.</summary>
internal interface IFfmpegRunner
{
    /// <summary>Starts ffmpeg; <paramref name="exited"/> is reported even for an immediate exit.</summary>
    /// <exception cref="Win32Exception">ffmpeg could not be started.</exception>
    IFfmpegProcess Start(FfmpegArgs args, Action<string> errorLine, Action exited);

    /// <summary>Runs to completion without blocking a thread; see <see cref="Ffmpeg.RunAsync"/> for timeout and errors.</summary>
    Task<FfmpegResult> RunAsync(FfmpegProgram program, FfmpegArgs args, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>The real ffmpeg via <see cref="Ffmpeg"/>.</summary>
internal sealed class FfmpegRunner : IFfmpegRunner
{
    public static readonly FfmpegRunner Instance = new();

    FfmpegRunner() { }

    public IFfmpegProcess Start(FfmpegArgs args, Action<string> errorLine, Action exited) =>
        new RunningFfmpeg(Ffmpeg.Start(Ffmpeg.Exe, args, null, errorLine, exited));

    public Task<FfmpegResult> RunAsync(FfmpegProgram program, FfmpegArgs args, TimeSpan timeout, CancellationToken cancellation) =>
        Ffmpeg.RunAsync(program == FfmpegProgram.Probe ? Ffmpeg.Probe : Ffmpeg.Exe, args, timeout, cancellation: cancellation);

    sealed class RunningFfmpeg : IFfmpegProcess
    {
        readonly Process process;

        public RunningFfmpeg(Process process) => this.process = process;

        public void RequestQuit()
        {
            try
            {
                process.StandardInput.Write('q');
                process.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }   // ffmpeg already exited
        }

        public bool WaitForExit(TimeSpan timeout) => process.WaitForExit((int)timeout.TotalMilliseconds);

        public void Kill() => Ffmpeg.Kill(process);

        public void Dispose() => process.Dispose();
    }
}
