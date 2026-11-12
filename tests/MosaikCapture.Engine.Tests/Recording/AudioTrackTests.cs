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

    /// <summary>Runs <paramref name="drive"/> on a fresh track; returns the left channel and the whole raw file.</summary>
    (float[] Left, byte[] File) Record(Action<AudioTrack> drive, long endTicks = 0)
    {
        string file = directory.File("track.f32");
        var track = new AudioTrack(file, client);
        track.Begin();
        drive(track);
        track.End(0, endTicks);
        byte[] bytes = File.ReadAllBytes(file);
        var left = new float[bytes.Length / 8];
        for (int i = 0; i < left.Length; i++) left[i] = BitConverter.ToSingle(bytes, i * 8);
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
    public void SuccessCodesAreNoFailure()
    {
        client.PacketSizeResult = 1;                          // S_FALSE
        client.Packet(0, 480, first: 1);
        bool first = false, second = false;

        var (left, _) = Record(track =>
        {
            client.NextGetBufferResult = Wasapi.BufferEmpty;  // the packet vanished after all
            first = track.Drain(0, out _);
            second = track.Drain(0, out _);                  // and the next round reads it normally
        });

        Assert.True(first, "AUDCLNT_S_BUFFER_EMPTY closed the track");
        Assert.True(second);
        Assert.Equal(480, left.Length);
        Assert.Equal(1, client.Released);                     // nothing to release for the empty one
    }

    [Theory]
    [InlineData("{a}", "{a}", false)]
    [InlineData("{a}", "{A}", false)]       // ids compare without case
    [InlineData("{a}", "{b}", true)]        // headphones plugged in: follow the new default
    [InlineData(null, "{b}", false)]        // not open: nothing to switch
    [InlineData("{a}", null, false)]        // no default device right now: keep recording
    public void SwitchesOnlyWhenTheDefaultDeviceReallyChanged(string? opened, string? current, bool reopen) =>
        Assert.Equal(reopen, AudioTrack.ShouldReopen(opened, current));

    [Fact]
    public void ReopeningOnAnotherDeviceKeepsTheTimeline()
    {
        string file = directory.File("switch.f32");
        var track = new AudioTrack(file, client);
        track.Begin();
        client.Packet(0, 480, first: 1);
        Drain(track);
        track.Close();
        Assert.Null(track.DeviceId);

        using var newDevice = new FakeCaptureClient();
        track.Attach(newDevice, "{new}");
        newDevice.Packet(Ticks(48_000), 480, first: 5000);   // the new device starts one second later
        Drain(track);
        Assert.Equal("{new}", track.DeviceId);
        track.End(0, 0);

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal((48_000 + 480) * 8, bytes.Length);
        Assert.Equal(0, BitConverter.ToSingle(bytes, 480 * 8));             // the gap is silence
        Assert.Equal(5000, BitConverter.ToSingle(bytes, 48_000 * 8));       // and the new sound sits at its time
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
    public void EndPadsToTheEndAndWritesRawFloatStereoWithoutHeader()
    {
        client.Packet(0, 480, first: 1);

        var (left, bytes) = Record(t => Drain(t), endTicks: TicksPerSecond);

        Assert.Equal(Rate, left.Length);
        Assert.Equal(Rate * 8, bytes.Length);       // nothing but samples
        Assert.Equal(1, left[0]);                   // the first sample is the first byte
        Assert.Equal(1, BitConverter.ToSingle(bytes, 4));   // right channel
    }

    [Theory]
    [InlineData(1.0, 48_000L)]
    [InlineData(5598.0, 268_704_000L)]                   // 93.3 minutes: where a signed 32-bit WAV size used to overflow
    [InlineData(4 * 3600.0, 691_200_000L)]               // four hours: 5.5 GB, beyond any RIFF size field
    public void LongRecordingsAreCountedIn64Bit(double seconds, long frames)
    {
        long end = (long)Math.Round(seconds * TicksPerSecond);

        Assert.Equal(frames, AudioTrack.FramesBetween(0, end));
        Assert.Equal(frames * 8, AudioTrack.BytesFor(frames));
        if (seconds > 5000) Assert.True(AudioTrack.BytesFor(frames) > int.MaxValue);
        if (seconds > 3 * 3600) Assert.True(AudioTrack.BytesFor(frames) > uint.MaxValue);
    }

    [Fact]
    public void FfmpegIsToldTheRawFormat() =>
        Assert.Equal(new[] { "-f", "f32le", "-ar", "48000", "-ac", "2" }, AudioTrack.InputArguments);

    [Fact]
    public void FailedReleaseIsLoggedAndDrainingGoesOn()
    {
        client.ReleaseResult = unchecked((int)0x88890004);
        client.Packet(0, 480, first: 1);
        client.Packet(Ticks(480), 480, first: 481);

        var (left, _) = Record(t => Drain(t));

        Assert.Equal(960, left.Length);
        Assert.Equal(2, client.Released);
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
            if (!FailNextPacketSize) return PacketSizeResult;
            FailNextPacketSize = false;
            return unchecked((int)0x88890004);   // AUDCLNT_E_DEVICE_INVALIDATED
        }

        /// <summary>Returned once by the next GetBuffer instead of a packet (e.g. AUDCLNT_S_BUFFER_EMPTY).</summary>
        public int? NextGetBufferResult { get; set; }

        /// <summary>Returned by GetNextPacketSize when it succeeds (0 = S_OK, 1 = S_FALSE).</summary>
        public int PacketSizeResult { get; set; }

        public int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
        {
            if (NextGetBufferResult is int result)
            {
                NextGetBufferResult = null;
                data = IntPtr.Zero;
                frames = flags = 0;
                devicePosition = qpcPosition = 0;
                return result;
            }
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

        public int ReleaseResult { get; set; }
        public int Released { get; private set; }

        public int ReleaseBuffer(uint frames)
        {
            Released++;
            Dispose();
            return ReleaseResult;
        }

        public void Dispose()
        {
            if (current == IntPtr.Zero) return;
            Marshal.FreeHGlobal(current);
            current = IntPtr.Zero;
        }
    }
}
