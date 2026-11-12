namespace MosaikCapture.Engine.Recording;

/// <summary>Result of looking up the GPU output of a monitor.</summary>
internal enum OutputLookup
{
    /// <summary>On the first GPU: ddagrab can record it.</summary>
    Found,
    /// <summary>Connected to another GPU: ffmpeg's ddagrab only uses the first adapter (it has no adapter option).</summary>
    OtherGpu,
    /// <summary>No GPU output shows it (e.g. a remote session or a mirrored display).</summary>
    NotFound,
}

/// <summary>Which GPU output shows a monitor (ffmpeg's ddagrab counts the outputs of the first adapter).</summary>
internal static class Dxgi
{
    const int MaxOutputs = 16, MaxAdapters = 8;

    public static OutputLookup Find(RECT monitor, out int outputIndex, out RECT desktop)
    {
        outputIndex = -1;
        desktop = monitor;
        var factoryId = typeof(IDXGIFactory1).GUID;
        if (NativeMethods.CreateDXGIFactory1(ref factoryId, out IntPtr factoryPointer) != 0) return OutputLookup.NotFound;
        IDXGIFactory1? factory = null;
        try
        {
            factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
            for (uint adapterIndex = 0; adapterIndex < MaxAdapters; adapterIndex++)
            {
                if (factory.EnumAdapters(adapterIndex, out IDXGIAdapter? adapter) != 0 || adapter == null) break;
                try
                {
                    int index = OutputOf(adapter, monitor, out desktop);
                    if (index < 0) continue;
                    if (adapterIndex > 0) return OutputLookup.OtherGpu;
                    outputIndex = index;
                    return OutputLookup.Found;
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            desktop = monitor;
            return OutputLookup.NotFound;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            ErrorLog.Write("Enumerating the GPU outputs", ex);
            return OutputLookup.NotFound;
        }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
            Marshal.Release(factoryPointer);   // the reference CreateDXGIFactory1 handed out
        }
    }

    // The index of the adapter's output that shows exactly this monitor, or -1.
    static int OutputOf(IDXGIAdapter adapter, RECT monitor, out RECT desktop)
    {
        desktop = monitor;
        for (uint index = 0; index < MaxOutputs; index++)
        {
            if (adapter.EnumOutputs(index, out IDXGIOutput? output) != 0 || output == null) break;
            int result = output.GetDesc(out DXGI_OUTPUT_DESC description);
            Marshal.ReleaseComObject(output);
            if (result != 0) continue;
            var bounds = description.DesktopCoordinates;
            if (bounds.left == monitor.left && bounds.top == monitor.top && bounds.right == monitor.right && bounds.bottom == monitor.bottom)
            {
                desktop = bounds;
                return (int)index;
            }
        }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop, Rotation;
        public IntPtr Monitor;
    }

    // Only the methods we need; the placeholders keep the vtable order.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter? adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput? output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC description);
    }
}
