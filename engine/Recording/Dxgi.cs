namespace MosaikCapture.Engine.Recording;

/// <summary>Which GPU output shows a monitor (ffmpeg's ddagrab counts the outputs of the first adapter).</summary>
internal static class Dxgi
{
    const int MaxOutputs = 16;

    public static bool Find(RECT monitor, out int outputIndex, out RECT desktop)
    {
        outputIndex = -1;
        desktop = monitor;
        var factoryId = typeof(IDXGIFactory1).GUID;
        if (NativeMethods.CreateDXGIFactory1(ref factoryId, out IntPtr factoryPointer) != 0) return false;
        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
        Marshal.Release(factoryPointer);
        IDXGIAdapter? adapter = null;
        try
        {
            if (factory.EnumAdapters(0, out adapter) != 0 || adapter == null) return false;
            for (uint index = 0; index < MaxOutputs; index++)
            {
                if (adapter.EnumOutputs(index, out IDXGIOutput? output) != 0 || output == null) break;
                int result = output.GetDesc(out DXGI_OUTPUT_DESC description);
                Marshal.ReleaseComObject(output);
                if (result != 0) continue;
                var bounds = description.DesktopCoordinates;
                if (bounds.left == monitor.left && bounds.top == monitor.top && bounds.right == monitor.right && bounds.bottom == monitor.bottom)
                {
                    outputIndex = (int)index;
                    desktop = bounds;
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            ErrorLog.Write("Enumerating the GPU outputs", ex);
            return false;
        }
        finally
        {
            if (adapter != null) Marshal.ReleaseComObject(adapter);
            Marshal.ReleaseComObject(factory);
        }
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
