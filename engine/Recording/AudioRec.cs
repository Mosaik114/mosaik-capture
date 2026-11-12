namespace MosaikCapture.Engine.Recording;

// Audio: system sound (loopback) and microphone as separate WAV tracks on a shared timeline.
class AudioRec
{
    public const int Rate = 48000;
    public readonly string SysFile, MicFile;
    public volatile float MicLevel;
    public volatile bool SysUsed, MicUsed;   // only tracks that were on at some point end up in the video
    readonly Track sys, mic;
    Thread? thread;
    volatile bool stop;
    long q0;   // start of the tracks (100 ns ticks)

    public AudioRec(string dir, bool micOn, bool soundOn)
    {
        SysFile = Path.Combine(dir, "ton.wav");
        MicFile = Path.Combine(dir, "mikro.wav");
        sys = new Track(SysFile, true) { Want = true, Mute = !soundOn };
        mic = new Track(MicFile, false) { Want = micOn };
        SysUsed = soundOn;
        MicUsed = micOn;
    }

    public void Start()
    {
        q0 = RecClock.Now100();
        thread = new Thread(Loop) { IsBackground = true, Name = "Mosaiks-Aufnahme Ton" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // Seconds from the start of the tracks to the given moment.
    public double Offset(long q100) { return (q100 - q0) / 1e7; }

    // The microphone is only opened while it is on (otherwise Windows permanently shows "microphone in use").
    public void SetMic(bool on)
    {
        mic.Want = on;
        if (on) MicUsed = true; else MicLevel = 0;
    }

    public void SetSound(bool on)
    {
        sys.Mute = !on;
        if (on) SysUsed = true;
    }

    public void Stop()
    {
        stop = true;
        thread?.Join(5000);
    }

    void Loop()
    {
        object? en = null;
        try
        {
            sys.Begin();
            mic.Begin();
            en = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            var e = (IMMDeviceEnumerator)en;
            while (!stop)
            {
                Serve(sys, e, false);
                Serve(mic, e, true);
                Thread.Sleep(10);
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        finally
        {
            long end = RecClock.Now100();
            sys.End(q0, end);
            mic.End(q0, end);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    void Serve(Track t, IMMDeviceEnumerator e, bool isMic)
    {
        if (t.Want && t.Client == null && unchecked(Environment.TickCount - t.Retry) >= 0)
        {
            if (!t.Open(e)) t.Retry = Environment.TickCount + 1000;
        }
        else if (!t.Want && t.Client != null) t.Close();
        if (t.Client == null) return;
        if (!t.Drain(q0, out float peak)) { t.Close(); t.Retry = Environment.TickCount + 500; }   // e.g. a different output device was selected
        if (isMic) MicLevel = peak;
    }

    // One track: packets arrive with timestamps; gaps (silence, microphone off) are filled with zeros.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The file stream is always closed by End(), which the capture loop calls in its finally block.")]
    class Track
    {
        const long Tol = Rate / 50;   // 20 ms tolerance before padding or trimming
        static readonly byte[] Zeros = new byte[Rate * 8 / 10];
        static IntPtr format;
        public volatile bool Want, Mute;
        public IAudioClient? Client;
        public int Retry;
        IAudioCaptureClient? cap;
        readonly string file;
        readonly bool loopback;
        FileStream? fs;
        long written;   // number of frames written (2 float channels each)
        byte[] buf = new byte[Rate * 8 / 5];

        public Track(string file, bool loopback)
        {
            this.file = file;
            this.loopback = loopback;
        }

        // 48 kHz, stereo, 32-bit float; Windows converts from the device format.
        static IntPtr Format
        {
            get
            {
                if (format != IntPtr.Zero) return format;
                IntPtr p = Marshal.AllocHGlobal(40);
                Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));   // WAVE_FORMAT_EXTENSIBLE
                Marshal.WriteInt16(p, 2, 2);
                Marshal.WriteInt32(p, 4, Rate);
                Marshal.WriteInt32(p, 8, Rate * 8);
                Marshal.WriteInt16(p, 12, 8);
                Marshal.WriteInt16(p, 14, 32);
                Marshal.WriteInt16(p, 16, 22);
                Marshal.WriteInt16(p, 18, 32);
                Marshal.WriteInt32(p, 20, 3);                         // front left + front right
                byte[] g = new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray();   // IEEE float
                Marshal.Copy(g, 0, p + 24, 16);
                format = p;
                return p;
            }
        }

        public void Begin()
        {
            fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            fs.Write(new byte[44], 0, 44);
        }

        public bool Open(IMMDeviceEnumerator e)
        {
            IMMDevice? dev = null;
            object? o = null;
            try
            {
                if (e.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev) != 0 || dev == null) return false;
                var iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                var c = (IAudioClient)o!;
                // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY | (LOOPBACK); 200 ms buffer
                uint flags = 0x80000000u | 0x08000000u | (loopback ? 0x00020000u : 0u);
                object? co = null;
                var ic = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                if (c.Initialize(0, flags, 2000000, 0, Format, IntPtr.Zero) != 0 || c.GetService(ref ic, out co) != 0 || c.Start() != 0)
                {
                    if (co != null) Marshal.ReleaseComObject(co);
                    Marshal.ReleaseComObject(c);
                    return false;
                }
                cap = (IAudioCaptureClient)co!;
                Client = c;
                return true;
            }
            catch (Exception) { return false; }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); }
        }

        public void Close()
        {
            try { Client?.Stop(); } catch (Exception) { }   // device already gone (unplugged or switched)
            if (cap != null) { Marshal.ReleaseComObject(cap); cap = null; }
            if (Client != null) { Marshal.ReleaseComObject(Client); Client = null; }
        }

        public bool Drain(long q0, out float peak)
        {
            peak = 0;
            while (true)
            {
                if (cap!.GetNextPacketSize(out uint n) != 0) return false;
                if (n == 0) return true;
                if (cap!.GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc) != 0) return false;
                // Where does the packet belong? (flag 4 = timestamp unreliable: just append)
                long target = (flags & 4) != 0 ? written : ((long)qpc - q0) * Rate / 10000000;
                if (target > written + Tol) Silence(target - written);
                long skip = target < written - Tol ? Math.Min(frames, written - target) : 0;
                int count = (int)(frames - skip);
                if (count > 0)
                {
                    if ((flags & 2) != 0 || Mute) Silence(count);   // 2 = silent
                    else
                    {
                        int bytes = count * 8;
                        if (buf.Length < bytes) buf = new byte[bytes];
                        Marshal.Copy(new IntPtr(data.ToInt64() + skip * 8), buf, 0, bytes);
                        fs!.Write(buf, 0, bytes);
                        written += count;
                        for (int i = 0; i < bytes; i += 16)   // level meter: every second frame is enough
                        {
                            float v = Math.Abs(BitConverter.ToSingle(buf, i));
                            if (v > peak) peak = v;
                        }
                    }
                }
                cap!.ReleaseBuffer(frames);
            }
        }

        void Silence(long frames)
        {
            long bytes = frames * 8;
            while (bytes > 0)
            {
                int k = (int)Math.Min(bytes, Zeros.Length);
                fs!.Write(Zeros, 0, k);
                bytes -= k;
            }
            written += frames;
        }

        // Pads with silence up to the end and writes the WAV header with the final sizes.
        public void End(long q0, long end)
        {
            Close();
            if (fs == null) return;
            try
            {
                long target = (end - q0) * Rate / 10000000;
                if (target > written) Silence(target - written);
                long data = written * 8;
                fs.Position = 0;
                var w = new BinaryWriter(fs);
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((int)Math.Min(int.MaxValue, 36 + data));
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)3);        // IEEE float
                w.Write((short)2);
                w.Write(Rate);
                w.Write(Rate * 8);
                w.Write((short)8);
                w.Write((short)32);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((int)Math.Min(int.MaxValue, data));
                w.Flush();
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally { fs.Dispose(); fs = null; }
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotUsed1();
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr prm, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
