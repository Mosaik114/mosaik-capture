namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// One screenshot: the whole frozen virtual screen (the editor can still change the crop), the
/// selected crop in device pixels and the monitor it is on.
/// </summary>
internal sealed class ScreenCapture
{
    /// <summary>The frozen virtual screen.</summary>
    public BitmapSource FullScreen { get; init; } = null!;
    /// <summary>The cropped (and possibly edited) image.</summary>
    public BitmapSource Image { get; set; } = null!;
    /// <summary>Crop relative to the top-left corner of the virtual screen (device pixels).</summary>
    public Int32Rect Crop { get; set; }
    public RECT Monitor { get; init; }
    public RECT WorkArea { get; init; }
    /// <summary>Position and size of the virtual screen (device pixels).</summary>
    public int VirtualLeft { get; init; }
    public int VirtualTop { get; init; }
    public int VirtualWidth { get; init; }
    public int VirtualHeight { get; init; }
    /// <summary>Device pixels per WPF unit.</summary>
    public double Scale { get; init; }
    /// <summary>Where the PNG is saved.</summary>
    public string Path { get; set; } = "";
}
