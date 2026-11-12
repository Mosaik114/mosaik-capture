namespace MosaikCapture.Engine.Recording;

/// <summary>
/// One audio track written to a WAV file (stereo 32-bit float). Packets arrive with timestamps;
/// gaps (silence, microphone off) are filled with zeros and overlaps are cut, so the file stays on
/// the shared timeline that starts at the recorder's start time.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The file stream is always closed by End(), which the capture loop calls in its finally block.")]
internal sealed class AudioTrack
{
    public const int Rate = 48000;
    const int BytesPerFrame = 8;                  // two float channels
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

    public long FramesWritten => framesWritten;

    public void Begin()
    {
        stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        stream.Write(new byte[44], 0, 44);   // room for the WAV header written by End()
    }

    /// <summary>Opens the default render (loopback) or capture device; false if not possible right now.</summary>
    public bool Open(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(loopback ? Wasapi.RenderFlow : Wasapi.CaptureFlow, Wasapi.ConsoleRole, out device) != 0 || device == null) return false;
            var clientId = Wasapi.AudioClientInterface;
            if (device.Activate(ref clientId, Wasapi.ClsctxAll, IntPtr.Zero, out object? instance) != 0 || instance == null) return false;
            var client = (IAudioClient)instance;
            uint flags = Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality | (loopback ? Wasapi.StreamFlagsLoopback : 0u);
            format = Wasapi.AllocateFloatStereoFormat(Rate);
            object? service = null;
            var captureId = Wasapi.CaptureClientInterface;
            const long BufferDuration = 2_000_000;   // 200 ms
            if (client.Initialize(Wasapi.SharedMode, flags, BufferDuration, 0, format, IntPtr.Zero) != 0 ||
                client.GetService(ref captureId, out service) != 0 || service == null || client.Start() != 0)
            {
                if (service != null) Marshal.ReleaseComObject(service);
                Marshal.ReleaseComObject(client);
                return false;
            }
            capture = (IAudioCaptureClient)service;
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
            if (source.GetNextPacketSize(out uint pending) != 0) return false;
            if (pending == 0) return true;
            if (source.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpcPosition) != 0) return false;
            long target = (flags & Wasapi.BufferFlagsTimestampError) != 0 ? framesWritten : ((long)qpcPosition - start) * Rate / TicksPerSecond;
            if (target > framesWritten + Tolerance) WriteSilence(target - framesWritten);
            long skip = target < framesWritten - Tolerance ? Math.Min(frames, framesWritten - target) : 0;
            int count = (int)(frames - skip);
            if (count > 0)
            {
                if ((flags & Wasapi.BufferFlagsSilent) != 0 || Mute) WriteSilence(count);
                else peak = Math.Max(peak, WriteSamples(new IntPtr(data.ToInt64() + skip * BytesPerFrame), count));
            }
            source.ReleaseBuffer(frames);
        }
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
        long bytes = frames * BytesPerFrame;
        while (bytes > 0)
        {
            int chunk = (int)Math.Min(bytes, Zeros.Length);
            stream!.Write(Zeros, 0, chunk);
            bytes -= chunk;
        }
        framesWritten += frames;
    }

    /// <summary>Pads with silence up to <paramref name="end"/> and writes the WAV header with the final sizes.</summary>
    public void End(long start, long end)
    {
        Close();
        if (stream == null) return;
        try
        {
            long target = (end - start) * Rate / TicksPerSecond;
            if (target > framesWritten) WriteSilence(target - framesWritten);
            long dataBytes = framesWritten * BytesPerFrame;
            stream.Position = 0;
            var header = new BinaryWriter(stream);
            header.Write(Encoding.ASCII.GetBytes("RIFF"));
            header.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
            header.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            header.Write(16);
            header.Write((short)3);   // IEEE float
            header.Write((short)2);
            header.Write(Rate);
            header.Write(Rate * BytesPerFrame);
            header.Write((short)BytesPerFrame);
            header.Write((short)32);
            header.Write(Encoding.ASCII.GetBytes("data"));
            header.Write((int)Math.Min(int.MaxValue, dataBytes));
            header.Flush();
        }
        catch (IOException ex) { ErrorLog.Write("Finishing " + Path.GetFileName(file), ex); }
        finally
        {
            stream.Dispose();
            stream = null;
        }
    }
}
