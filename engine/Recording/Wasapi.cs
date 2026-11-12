namespace MosaikCapture.Engine.Recording;

// The parts of the Windows Audio Session API (WASAPI) the recorder uses. Methods are declared in
// vtable order; unused leading methods are placeholders.

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int NotUsed1();
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

internal static class Wasapi
{
    public static readonly Guid DeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid AudioClientInterface = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid CaptureClientInterface = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public const int RenderFlow = 0, CaptureFlow = 1;
    public const int ConsoleRole = 0;
    public const int ClsctxAll = 23;
    public const int SharedMode = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;

    /// <summary>
    /// Allocates a WAVEFORMATEXTENSIBLE for stereo 32-bit float at <paramref name="rate"/>;
    /// free it with <see cref="Marshal.FreeHGlobal"/>. Windows converts from the device format.
    /// </summary>
    public static IntPtr AllocateFloatStereoFormat(int rate)
    {
        var format = FloatStereoFormat(rate);
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
        Marshal.StructureToPtr(format, memory, false);
        return memory;
    }

    /// <summary>Stereo 32-bit float at <paramref name="rate"/>, as WASAPI expects it.</summary>
    internal static WAVEFORMATEXTENSIBLE FloatStereoFormat(int rate)
    {
        const ushort Channels = 2, BitsPerSample = 32, BlockAlign = Channels * BitsPerSample / 8;
        return new WAVEFORMATEXTENSIBLE
        {
            FormatTag = WaveFormatExtensible,
            Channels = Channels,
            SamplesPerSecond = (uint)rate,
            AverageBytesPerSecond = (uint)rate * BlockAlign,
            BlockAlign = BlockAlign,
            BitsPerSample = BitsPerSample,
            ExtensionSize = 22,   // the bytes after the WAVEFORMATEX part
            ValidBitsPerSample = BitsPerSample,
            ChannelMask = SpeakerFrontLeft | SpeakerFrontRight,
            SubFormat = IeeeFloatSubFormat,
        };
    }

    const ushort WaveFormatExtensible = 0xFFFE;
    const uint SpeakerFrontLeft = 0x1, SpeakerFrontRight = 0x2;

    /// <summary>WAVEFORMATEXTENSIBLE (mmreg.h): packed, 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEXTENSIBLE
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtensionSize;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }
}
