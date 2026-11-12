namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Records system sound (loopback) and the microphone via WASAPI into two WAV files on a shared
/// timeline, so they can be laid under the video segments with exact timing.
/// </summary>
internal sealed class AudioRecorder
{
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    readonly AudioTrack systemSound, microphone;
    Thread? thread;
    volatile bool stopping, failed;
    volatile float microphoneLevel;
    long startTicks;   // start of the timeline (100 ns ticks)

    public AudioRecorder(string directory, bool microphoneOn, bool systemSoundOn)
    {
        SystemSoundFile = Path.Combine(directory, "system.wav");
        MicrophoneFile = Path.Combine(directory, "microphone.wav");
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

    public void Start()
    {
        startTicks = Clocks.Now100Ns();
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Mosaik Capture audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Seconds from the start of the tracks to the given moment (100 ns ticks).</summary>
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
    /// Stops recording and finalizes the WAV files. False if the capture thread did not finish in
    /// time or failed: then the files must not be used.
    /// </summary>
    public bool Stop()
    {
        stopping = true;
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
            while (!stopping)
            {
                Serve(systemSound, enumerator, isMicrophone: false);
                Serve(microphone, enumerator, isMicrophone: true);
                Thread.Sleep(10);
            }
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
        }
    }

    void Serve(AudioTrack track, IMMDeviceEnumerator enumerator, bool isMicrophone)
    {
        if (track.Want && track.Client == null && unchecked(Environment.TickCount - track.RetryAt) >= 0)
        {
            if (!track.Open(enumerator)) track.RetryAt = Environment.TickCount + 1000;
        }
        else if (!track.Want && track.Client != null) track.Close();
        if (track.Client == null) return;
        if (!track.Drain(startTicks, out float peak))
        {
            track.Close();   // e.g. a different output device was selected
            track.RetryAt = Environment.TickCount + 500;
        }
        if (isMicrophone) microphoneLevel = peak;
    }
}
