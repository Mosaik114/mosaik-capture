namespace MosaikCapture.Engine.Recording;

/// <summary>
/// One audio track written as raw samples (stereo 32-bit float, 48 kHz, no header: a WAV header cannot
/// describe more than 4 GB, about three hours, and its signed sizes break after 93 minutes). Packets arrive with timestamps;
/// gaps (silence, microphone off) are filled with zeros and overlaps are cut, so the file stays on
/// the shared timeline that starts at the recorder's start time.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The file stream is always closed by End(), which the capture loop calls in its finally block.")]
internal sealed class AudioTrack
{
    public const int Rate = 48000, Channels = 2;
    const int BytesPerFrame = 4 * Channels;       // 32-bit float per channel

    /// <summary>
    /// ffmpeg input options for a track file. The format is fixed: the capture client is opened with exactly
    /// this mix format and AUTOCONVERTPCM, so Windows converts whatever the device uses.
    /// </summary>
    public static readonly string[] InputArguments = { "-f", "f32le", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-ac", Channels.ToString(CultureInfo.InvariantCulture) };
    const long Tolerance = Rate / 50;             // 20 ms before padding or trimming
    const long TicksPerSecond = 10_000_000;       // QPC positions are in 100 ns units
    static readonly byte[] Zeros = new byte[Rate * BytesPerFrame / 10];

    readonly string file;
    readonly bool loopback;
    IAudioCaptureClient? capture;
    FileStream? stream;
    long framesWritten;
    byte[] buffer = new byte[Rate * BytesPerFrame / 5];

    public AudioTrack(string file, bool loopback)
    {
        this.file = file;
        this.loopback = loopback;
    }

    /// <summary>Tests: a track fed by a fake capture client instead of a device.</summary>
    internal AudioTrack(string file, IAudioCaptureClient capture) : this(file, false) => this.capture = capture;

    public volatile bool Want, Mute;
    public IAudioClient? Client { get; private set; }
    public int RetryAt { get; set; }

    /// <summary>Environment.TickCount when the device should be compared with the default again.</summary>
    public int DeviceCheckAt { get; set; }

    public long FramesWritten => framesWritten;

    /// <summary>Endpoint id of the open device, or null when closed.</summary>
    public string? DeviceId { get; private set; }

    /// <summary>
    /// True if Windows' default device is now a different one than the track records from (e.g. headphones
    /// plugged in). The track then reopens on the new device; the gap is padded with silence on the timeline.
    /// </summary>
    internal static bool ShouldReopen(string? openedDevice, string? defaultDevice) =>
        openedDevice != null && defaultDevice != null && !string.Equals(openedDevice, defaultDevice, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of Windows' current default device for this track, or null if there is none.</summary>
    public string? DefaultDeviceId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        try
        {
            if (Wasapi.Failed(enumerator.GetDefaultAudioEndpoint(loopback ? Wasapi.RenderFlow : Wasapi.CaptureFlow, Wasapi.ConsoleRole, out device)) || device == null) return null;
            return Wasapi.Failed(device.GetId(out string? id)) ? null : id;
        }
        catch (COMException ex)
        {
            ErrorLog.Write("Reading the default audio device", ex);
            return null;
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Records from <paramref name="source"/> from now on (also: tests with a fake client).</summary>
    internal void Attach(IAudioCaptureClient source, string? deviceId)
    {
        capture = source;
        DeviceId = deviceId;
    }

    public void Begin()
    {
        stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
    }

    /// <summary>Opens the default render (loopback) or capture device; false if not possible right now.</summary>
    public bool Open(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            if (Wasapi.Failed(enumerator.GetDefaultAudioEndpoint(loopback ? Wasapi.RenderFlow : Wasapi.CaptureFlow, Wasapi.ConsoleRole, out device)) || device == null) return false;
            string? deviceId = Wasapi.Failed(device.GetId(out string? id)) ? null : id;
            var clientId = Wasapi.AudioClientInterface;
            if (Wasapi.Failed(device.Activate(ref clientId, Wasapi.ClsctxAll, IntPtr.Zero, out object? instance)) || instance == null) return false;
            var client = (IAudioClient)instance;
            uint flags = Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality | (loopback ? Wasapi.StreamFlagsLoopback : 0u);
            format = Wasapi.AllocateFloatStereoFormat(Rate);
            object? service = null;
            var captureId = Wasapi.CaptureClientInterface;
            const long BufferDuration = 2_000_000;   // 200 ms
            if (Wasapi.Failed(client.Initialize(Wasapi.SharedMode, flags, BufferDuration, 0, format, IntPtr.Zero)) ||
                Wasapi.Failed(client.GetService(ref captureId, out service)) || service == null || Wasapi.Failed(client.Start()))
            {
                if (service != null) Marshal.ReleaseComObject(service);
                Marshal.ReleaseComObject(client);
                return false;
            }
            Attach((IAudioCaptureClient)service, deviceId);
            Client = client;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            ErrorLog.Write(loopback ? "Opening the system sound" : "Opening the microphone", ex);
            return false;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeHGlobal(format);
            if (device != null) Marshal.ReleaseComObject(device);
        }
    }

    public void Close()
    {
        try { Client?.Stop(); }
        catch (COMException) { }   // device already gone (unplugged or switched)
        if (capture != null && Marshal.IsComObject(capture)) Marshal.ReleaseComObject(capture);
        capture = null;
        DeviceId = null;
        if (Client != null) { Marshal.ReleaseComObject(Client); Client = null; }
    }

    /// <summary>
    /// Writes all pending packets. <paramref name="start"/> is the start of the timeline in 100 ns
    /// ticks; <paramref name="peak"/> is the loudest sample for the level meter. False if the device failed.
    /// </summary>
    public bool Drain(long start, out float peak)
    {
        peak = 0;
        var source = capture ?? throw new InvalidOperationException("The track is not open.");
        while (true)
        {
            if (Wasapi.Failed(source.GetNextPacketSize(out uint pending))) return false;
            if (pending == 0) return true;
            int got = source.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpcPosition);
            if (Wasapi.Failed(got)) return false;
            if (got == Wasapi.BufferEmpty) return true;   // nothing to read after all (no buffer to release)
            try
            {
                peak = Math.Max(peak, Write(data, frames, flags, (long)qpcPosition - start));
            }
            finally
            {
                int released = source.ReleaseBuffer(frames);   // also after a write error, or the device stalls
                if (Wasapi.Failed(released)) ErrorLog.Write("Releasing an audio buffer failed: 0x" + released.ToString("X8", CultureInfo.InvariantCulture), null);
            }
        }
    }

    // Places one packet on the timeline: pads a gap, cuts an overlap, writes the rest; returns its peak.
    float Write(IntPtr data, uint frames, uint flags, long ticksSinceStart)
    {
        long target = (flags & Wasapi.BufferFlagsTimestampError) != 0 ? framesWritten : ticksSinceStart * Rate / TicksPerSecond;
        if (target > framesWritten + Tolerance) WriteSilence(target - framesWritten);
        long skip = target < framesWritten - Tolerance ? Math.Min(frames, framesWritten - target) : 0;
        int count = (int)(frames - skip);
        if (count <= 0) return 0;
        if ((flags & Wasapi.BufferFlagsSilent) != 0 || Mute)
        {
            WriteSilence(count);
            return 0;
        }
        return WriteSamples(new IntPtr(data.ToInt64() + skip * BytesPerFrame), count);
    }

    // Copies the samples and returns their peak; every second frame is enough for the meter.
    float WriteSamples(IntPtr data, int frames)
    {
        int bytes = frames * BytesPerFrame;
        if (buffer.Length < bytes) buffer = new byte[bytes];
        Marshal.Copy(data, buffer, 0, bytes);
        stream!.Write(buffer, 0, bytes);
        framesWritten += frames;
        float peak = 0;
        for (int i = 0; i < bytes; i += 2 * BytesPerFrame) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        return peak;
    }

    void WriteSilence(long frames)
    {
        long bytes = BytesFor(frames);
        while (bytes > 0)
        {
            int chunk = (int)Math.Min(bytes, Zeros.Length);
            stream!.Write(Zeros, 0, chunk);
            bytes -= chunk;
        }
        framesWritten += frames;
    }

    /// <summary>Frames on the timeline from <paramref name="start"/> to <paramref name="end"/> (100 ns ticks); 64-bit, so hours of audio fit.</summary>
    internal static long FramesBetween(long start, long end) => (end - start) * Rate / TicksPerSecond;

    /// <summary>Bytes of <paramref name="frames"/> frames on disk.</summary>
    internal static long BytesFor(long frames) => frames * BytesPerFrame;

    /// <summary>Pads with silence up to <paramref name="end"/> so the track covers the whole timeline, then closes the file.</summary>
    public void End(long start, long end)
    {
        Close();
        if (stream == null) return;
        try
        {
            long target = FramesBetween(start, end);
            if (target > framesWritten) WriteSilence(target - framesWritten);
            stream.Flush();
        }
        catch (IOException ex) { ErrorLog.Write("Finishing " + Path.GetFileName(file), ex); }
        finally
        {
            stream.Dispose();
            stream = null;
        }
    }
}
