namespace MosaikCapture.Engine.Recording;

// Which GPU output shows this monitor? (ddagrab counts the outputs of the first adapter.)
static class Dxgi
{
    public static bool Find(ShotOverlay.RECT mon, out int index, out ShotOverlay.RECT desk)
    {
        index = -1;
        desk = mon;
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out IntPtr f) != 0) return false;
        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(f);
        Marshal.Release(f);
        IDXGIAdapter? adapter = null;
        try
        {
            if (factory.EnumAdapters(0, out adapter) != 0) return false;
            for (uint i = 0; i < 16; i++)
            {
                if (adapter.EnumOutputs(i, out IDXGIOutput o) != 0) break;
                int hr = o.GetDesc(out DXGI_OUTPUT_DESC d);
                Marshal.ReleaseComObject(o);
                if (hr != 0) continue;
                if (d.Desktop.left == mon.left && d.Desktop.top == mon.top && d.Desktop.right == mon.right && d.Desktop.bottom == mon.bottom)
                {
                    index = (int)i;
                    desk = d.Desktop;
                    return true;
                }
            }
            return false;
        }
        catch (Exception) { return false; }
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
        public ShotOverlay.RECT Desktop;
        public int Attached, Rotation;
        public IntPtr Monitor;
    }

    // Only the methods we need; the placeholders keep the vtable order.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int EnumAdapters(uint i, out IDXGIAdapter a);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int EnumOutputs(uint i, out IDXGIOutput o);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void F1(); void F2(); void F3(); void F4();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC d);
    }

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
