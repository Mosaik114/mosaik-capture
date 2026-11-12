using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

// ---------- Ton: Systemton (Loopback) und Mikro als eigene WAV-Spuren mit gemeinsamer Zeitachse ----------

class AudioRec
{
    public const int Rate = 48000;
    public readonly string SysFile, MicFile;
    public volatile float MicLevel;
    public volatile bool SysUsed, MicUsed;   // nur Spuren, die irgendwann an waren, kommen ins Video
    readonly Track sys, mic;
    Thread thread;
    volatile bool stop;
    long q0;   // Beginn der Spuren (100-ns-Schritte)

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

    // Sekunden vom Beginn der Spuren bis zu diesem Moment
    public double Offset(long q100) { return (q100 - q0) / 1e7; }

    // Mikro wird nur geoeffnet, solange es an ist (Windows zeigt sonst dauerhaft "Mikrofon in Benutzung")
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
        if (thread != null) thread.Join(5000);
    }

    void Loop()
    {
        object en = null;
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
        catch (Exception ex) { Program.Log(ex); }
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
        float peak;
        if (!t.Drain(q0, out peak)) { t.Close(); t.Retry = Environment.TickCount + 500; }   // z. B. anderes Ausgabegeraet gewaehlt
        if (isMic) MicLevel = peak;
    }

    // Eine Spur: Pakete kommen mit Zeitstempel; Luecken (Stille, Mikro aus) werden mit Nullen gefuellt
    class Track
    {
        const long Tol = Rate / 50;   // 20 ms Spielraum, bevor wir auffuellen oder kuerzen
        static readonly byte[] Zeros = new byte[Rate * 8 / 10];
        static IntPtr format;
        public volatile bool Want, Mute;
        public IAudioClient Client;
        public int Retry;
        IAudioCaptureClient cap;
        readonly string file;
        readonly bool loopback;
        FileStream fs;
        long written;   // Anzahl geschriebener Frames (je 2 Kanaele float)
        byte[] buf = new byte[Rate * 8 / 5];

        public Track(string file, bool loopback)
        {
            this.file = file;
            this.loopback = loopback;
        }

        // 48 kHz, Stereo, 32-Bit-Float; Windows rechnet das Geraeteformat selbst um
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
                Marshal.WriteInt32(p, 20, 3);                         // links + rechts
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
            IMMDevice dev = null;
            object o = null;
            try
            {
                if (e.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev) != 0 || dev == null) return false;
                var iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                var c = (IAudioClient)o;
                // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY | (LOOPBACK); Puffer 200 ms
                uint flags = 0x80000000u | 0x08000000u | (loopback ? 0x00020000u : 0u);
                object co = null;
                var ic = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                if (c.Initialize(0, flags, 2000000, 0, Format, IntPtr.Zero) != 0 || c.GetService(ref ic, out co) != 0 || c.Start() != 0)
                {
                    if (co != null) Marshal.ReleaseComObject(co);
                    Marshal.ReleaseComObject(c);
                    return false;
                }
                cap = (IAudioCaptureClient)co;
                Client = c;
                return true;
            }
            catch (Exception) { return false; }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); }
        }

        public void Close()
        {
            try { if (Client != null) Client.Stop(); } catch (Exception) { }   // Geraet schon weg (abgesteckt, gewechselt)
            if (cap != null) { Marshal.ReleaseComObject(cap); cap = null; }
            if (Client != null) { Marshal.ReleaseComObject(Client); Client = null; }
        }

        public bool Drain(long q0, out float peak)
        {
            peak = 0;
            while (true)
            {
                uint n;
                if (cap.GetNextPacketSize(out n) != 0) return false;
                if (n == 0) return true;
                IntPtr data;
                uint frames, flags;
                ulong devPos, qpc;
                if (cap.GetBuffer(out data, out frames, out flags, out devPos, out qpc) != 0) return false;
                // Wo gehoert das Paket hin? (Flag 4 = Zeitstempel unbrauchbar: einfach anhaengen)
                long target = (flags & 4) != 0 ? written : ((long)qpc - q0) * Rate / 10000000;
                if (target > written + Tol) Silence(target - written);
                long skip = target < written - Tol ? Math.Min(frames, written - target) : 0;
                int count = (int)(frames - skip);
                if (count > 0)
                {
                    if ((flags & 2) != 0 || Mute) Silence(count);   // 2 = Stille
                    else
                    {
                        int bytes = count * 8;
                        if (buf.Length < bytes) buf = new byte[bytes];
                        Marshal.Copy(new IntPtr(data.ToInt64() + skip * 8), buf, 0, bytes);
                        fs.Write(buf, 0, bytes);
                        written += count;
                        for (int i = 0; i < bytes; i += 16)   // Pegel fuer die Anzeige: jeder zweite Frame reicht
                        {
                            float v = Math.Abs(BitConverter.ToSingle(buf, i));
                            if (v > peak) peak = v;
                        }
                    }
                }
                cap.ReleaseBuffer(frames);
            }
        }

        void Silence(long frames)
        {
            long bytes = frames * 8;
            while (bytes > 0)
            {
                int k = (int)Math.Min(bytes, Zeros.Length);
                fs.Write(Zeros, 0, k);
                bytes -= k;
            }
            written += frames;
        }

        // Bis zum Ende mit Stille auffuellen und den WAV-Kopf mit den echten Groessen schreiben
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
            catch (Exception ex) { Program.Log(ex); }
            finally { fs.Dispose(); fs = null; }
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotUsed1();
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr prm, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
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
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
