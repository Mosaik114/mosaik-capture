namespace MosaikCapture.Engine.Recording;

/// <summary>The audio side of a recording (a seam so the session can be tested without devices).</summary>
internal interface IAudioRecorder
{
    string SystemSoundFile { get; }
    string MicrophoneFile { get; }
    bool SystemSoundUsed { get; }
    bool MicrophoneUsed { get; }
    float MicrophoneLevel { get; }
    /// <summary>Completes when the capture thread has ended and the track files are closed.</summary>
    Task Finished { get; }
    void Start();
    /// <summary>Stops and finalizes the track files; false if they must not be used.</summary>
    bool Stop();
    /// <summary>Seconds from the start of the tracks to the given moment (100 ns ticks).</summary>
    double Offset(long ticks);
    void SetMicrophone(bool on);
    void SetSystemSound(bool on);
}

/// <summary>
/// Records system sound (loopback) and the microphone via WASAPI into two raw sample files on a shared
/// timeline, so they can be laid under the video segments with exact timing.
/// </summary>
/// <remarks>
/// The capture thread polls both devices every <see cref="PollInterval"/> (WASAPI loopback cannot be
/// event-driven in shared mode on all Windows 10 versions). The capture buffers hold 200 ms, so a 25 ms
/// poll drains them long before they overflow, even when a poll is delayed by a busy system. Stopping wakes it at once through
/// <c>stopSignal</c>; if it still does not end in time, <see cref="Finished"/> tells when the files are
/// released so their folder can be removed afterwards.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The stop signal lives as long as the recording; it is only set, never waited on after the capture thread ended.")]
internal sealed class AudioRecorder : IAudioRecorder
{
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    const int OpenRetryMs = 1000, DeviceChangeRetryMs = 500;

    /// <summary>How often the open tracks compare their device with Windows' current default device.</summary>
    const int DeviceCheckMs = 1000;

    readonly AudioTrack systemSound, microphone;
    readonly ManualResetEventSlim stopSignal = new();
    readonly TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread? thread;
    volatile bool failed;
    volatile float microphoneLevel;
    long startTicks;   // start of the timeline (100 ns ticks)

    public AudioRecorder(string directory, bool microphoneOn, bool systemSoundOn)
    {
        SystemSoundFile = Path.Combine(directory, "system.f32");
        MicrophoneFile = Path.Combine(directory, "microphone.f32");
        systemSound = new AudioTrack(SystemSoundFile, loopback: true) { Want = true, Mute = !systemSoundOn };
        microphone = new AudioTrack(MicrophoneFile, loopback: false) { Want = microphoneOn };
        SystemSoundUsed = systemSoundOn;
        MicrophoneUsed = microphoneOn;
    }

    public string SystemSoundFile { get; }
    public string MicrophoneFile { get; }

    // Only tracks that were on at some point end up in the video.
    public bool SystemSoundUsed { get; private set; }
    public bool MicrophoneUsed { get; private set; }

    public float MicrophoneLevel => microphoneLevel;

    /// <summary>True if the capture loop failed; the tracks may be incomplete.</summary>
    public bool Failed => failed;

    public Task Finished => thread == null ? Task.CompletedTask : finished.Task;

    public void Start()
    {
        startTicks = Clocks.Now100Ns();
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Mosaik Capture audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public double Offset(long ticks) => (ticks - startTicks) / 1e7;

    // The microphone is only opened while it is on (otherwise Windows permanently shows "microphone in use").
    public void SetMicrophone(bool on)
    {
        microphone.Want = on;
        if (on) MicrophoneUsed = true;
        else microphoneLevel = 0;
    }

    public void SetSystemSound(bool on)
    {
        systemSound.Mute = !on;
        if (on) SystemSoundUsed = true;
    }

    /// <summary>
    /// Stops recording and finalizes the track files. False if the capture thread did not finish in
    /// time or failed: then the files must not be used.
    /// </summary>
    public bool Stop()
    {
        stopSignal.Set();
        if (thread == null) return true;
        if (!thread.Join(StopTimeout))
        {
            ErrorLog.Write("The audio thread did not stop in time; the video is saved without sound", null);
            return false;
        }
        return !failed;
    }

    void CaptureLoop()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            systemSound.Begin();
            microphone.Begin();
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(Wasapi.DeviceEnumeratorClass)!)!;
            do
            {
                Serve(systemSound, enumerator, isMicrophone: false);
                Serve(microphone, enumerator, isMicrophone: true);
            }
            while (!stopSignal.Wait(PollInterval));
        }
        catch (Exception ex)   // any failure here must not crash the process; the recording continues without sound
        {
            failed = true;
            ErrorLog.Write("Recording audio", ex);
        }
        finally
        {
            long end = Clocks.Now100Ns();
            systemSound.End(startTicks, end);
            microphone.End(startTicks, end);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            finished.TrySetResult(true);
        }
    }

    void Serve(AudioTrack track, IMMDeviceEnumerator enumerator, bool isMicrophone)
    {
        if (track.Want && track.Client == null && unchecked(Environment.TickCount - track.RetryAt) >= 0)
        {
            if (!track.Open(enumerator)) track.RetryAt = Environment.TickCount + OpenRetryMs;
        }
        else if (!track.Want && track.Client != null) track.Close();
        if (track.Client == null) return;
        if (!track.Drain(startTicks, out float peak))
        {
            track.Close();   // the device failed (e.g. unplugged or disabled): try again shortly
            track.RetryAt = Environment.TickCount + DeviceChangeRetryMs;
        }
        else if (unchecked(Environment.TickCount - track.DeviceCheckAt) >= 0)
        {
            track.DeviceCheckAt = Environment.TickCount + DeviceCheckMs;
            if (AudioTrack.ShouldReopen(track.DeviceId, track.DefaultDeviceId(enumerator)))
            {
                ErrorLog.Write("The default audio device changed; recording from the new one", null);
                track.Close();   // reopened on the next round; the gap becomes silence on the timeline
                track.RetryAt = Environment.TickCount;
            }
        }
        if (isMicrophone) microphoneLevel = peak;
    }
}
