using System.Runtime.InteropServices;

namespace MosaikCapture.Engine.Tests.Recording;

// AudioTrack keeps every WAV file on the shared timeline: gaps become silence, overlaps are cut,
// packets with broken timestamps are appended. Fed by a fake WASAPI capture client.
public sealed class AudioTrackTests : IDisposable
{
    const int Rate = AudioTrack.Rate;
    const long TicksPerSecond = 10_000_000;
    const uint Silent = 0x2, TimestampError = 0x4;

    readonly TempDirectory directory = new();
    readonly FakeCaptureClient client = new();

    public void Dispose()
    {
        client.Dispose();
        directory.Dispose();
    }

    static long Ticks(long frames) => frames * TicksPerSecond / Rate;

    /// <summary>Runs <paramref name="drive"/> on a fresh track; returns the left channel and the whole WAV file.</summary>
    (float[] Left, byte[] File) Record(Action<AudioTrack> drive, long endTicks = 0)
    {
        string file = directory.File("track.wav");
        var track = new AudioTrack(file, client);
        track.Begin();
        drive(track);
        track.End(0, endTicks);
        byte[] bytes = File.ReadAllBytes(file);
        var left = new float[(bytes.Length - 44) / 8];
        for (int i = 0; i < left.Length; i++) left[i] = BitConverter.ToSingle(bytes, 44 + i * 8);
        return (left, bytes);
    }

    static float Drain(AudioTrack track)
    {
        Assert.True(track.Drain(0, out float peak));
        return peak;
    }

    [Fact]
    public void ContiguousPacketsAreWrittenAsTheyCome()
    {
        client.Packet(0, 480, first: 1);
        client.Packet(Ticks(480), 480, first: 481);

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(960, left.Length);
        Assert.Equal(Enumerable.Range(1, 960).Select(i => (float)i), left);
    }

    [Fact]
    public void GapsAreFilledWithSilence()
    {
        client.Packet(0, 480, first: 1);
        client.Packet(Ticks(4800), 480, first: 1000);   // 100 ms later

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(5280, left.Length);
        Assert.All(left.Skip(480).Take(4320), sample => Assert.Equal(0, sample));
        Assert.Equal(1000, left[4800]);
    }

    [Fact]
    public void SmallJitterIsNotPadded()
    {
        client.Packet(0, 480, first: 1);
        client.Packet(Ticks(480 + 900), 480, first: 1000);   // 19 ms late: within the 20 ms tolerance

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(960, left.Length);
        Assert.Equal(1000, left[480]);
    }

    [Fact]
    public void OverlapsAreCutSoTheTimelineStaysInPlace()
    {
        client.Packet(0, 4800, first: 1);
        client.Packet(Ticks(2400), 4800, first: 10_001);   // starts 50 ms before the previous one ended

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(7200, left.Length);
        Assert.Equal(4800, left[4799]);
        Assert.Equal(10_001 + 2400, left[4800]);   // the overlapping first half is skipped
    }

    [Fact]
    public void PacketsWithTimestampErrorsAreAppended()
    {
        client.Packet(0, 480, first: 1);
        client.Packet(Ticks(48_000), 480, first: 1000, flags: TimestampError);

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(960, left.Length);
        Assert.Equal(1000, left[480]);
    }

    [Fact]
    public void SilentPacketsAndMuteWriteZerosAndNoLevel()
    {
        client.Packet(0, 480, first: 1, flags: Silent);
        float silentPeak = 0, mutedPeak = 0;

        var (left, _) = Record(track =>
        {
            silentPeak = Drain(track);
            track.Mute = true;
            client.Packet(Ticks(480), 480, first: 1);
            mutedPeak = Drain(track);
        });

        Assert.Equal(960, left.Length);
        Assert.All(left, sample => Assert.Equal(0, sample));
        Assert.Equal(0, silentPeak);
        Assert.Equal(0, mutedPeak);
    }

    [Fact]
    public void PeakIsTheLoudestSampleForTheMeter()
    {
        client.Packet(0, 4, constant: -0.75f);

        float peak = 0;
        Record(track => peak = Drain(track));

        Assert.Equal(0.75f, peak);
    }

    [Fact]
    public void FailingDeviceStopsTheDrain()
    {
        client.FailNextPacketSize = true;
        bool ok = true;

        Record(track => ok = track.Drain(0, out _));

        Assert.False(ok);
    }

    [Fact]
    public void EndPadsToTheEndAndWritesAFloatStereoWavHeader()
    {
        client.Packet(0, 480, first: 1);

        var (left, bytes) = Record(t => Drain(t), endTicks: TicksPerSecond);

        Assert.Equal(Rate, left.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(36 + Rate * 8, BitConverter.ToInt32(bytes, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(16, BitConverter.ToInt32(bytes, 16));
        Assert.Equal(3, BitConverter.ToInt16(bytes, 20));        // IEEE float
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));        // stereo
        Assert.Equal(Rate, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(Rate * 8, BitConverter.ToInt32(bytes, 28));
        Assert.Equal(8, BitConverter.ToInt16(bytes, 32));
        Assert.Equal(32, BitConverter.ToInt16(bytes, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(Rate * 8, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(44 + Rate * 8, bytes.Length);
    }

    [Fact]
    public void EndNeverCutsWhatWasRecorded()
    {
        client.Packet(0, 4800, first: 1);

        var (left, _) = Record(t => Drain(t), endTicks: Ticks(100));

        Assert.Equal(4800, left.Length);
    }

    /// <summary>Hands out queued packets like IAudioCaptureClient (stereo float, both channels equal).</summary>
    sealed class FakeCaptureClient : IAudioCaptureClient, IDisposable
    {
        readonly Queue<(ulong Qpc, uint Flags, float[] Samples)> packets = new();
        IntPtr current;

        public bool FailNextPacketSize { get; set; }

        /// <summary>Frames numbered from <paramref name="first"/> upwards, or all equal to <paramref name="constant"/>.</summary>
        public void Packet(long qpc, int frames, int first = 0, uint flags = 0, float? constant = null)
        {
            var samples = new float[frames * 2];
            for (int i = 0; i < frames; i++) samples[2 * i] = samples[2 * i + 1] = constant ?? first + i;
            packets.Enqueue(((ulong)qpc, flags, samples));
        }

        public int GetNextPacketSize(out uint frames)
        {
            frames = packets.Count > 0 ? (uint)packets.Peek().Samples.Length / 2 : 0;
            if (!FailNextPacketSize) return 0;
            FailNextPacketSize = false;
            return unchecked((int)0x88890004);   // AUDCLNT_E_DEVICE_INVALIDATED
        }

        public int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
        {
            var (qpc, packetFlags, samples) = packets.Dequeue();
            current = Marshal.AllocHGlobal(samples.Length * sizeof(float));
            Marshal.Copy(samples, 0, current, samples.Length);
            data = current;
            frames = (uint)samples.Length / 2;
            flags = packetFlags;
            devicePosition = 0;
            qpcPosition = qpc;
            return 0;
        }

        public int ReleaseBuffer(uint frames)
        {
            Dispose();
            return 0;
        }

        public void Dispose()
        {
            if (current == IntPtr.Zero) return;
            Marshal.FreeHGlobal(current);
            current = IntPtr.Zero;
        }
    }
}
