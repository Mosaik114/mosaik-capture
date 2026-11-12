namespace MosaikCapture.Engine.Screenshot;

// One capture: the whole frozen image (for the editor, which can still change the crop),
// the selected crop in device pixels and the monitor it is on.
class ShotData
{
    public BitmapSource Full = null!, Image = null!;   // set by the overlay
    public Int32Rect Cut;                  // relative to the top-left corner of the virtual screen
    public ShotOverlay.RECT Monitor, Work;
    public int Vx, Vy, Vw, Vh;
    public double Scale;                   // device pixels per WPF unit
    public string Path = "";
}
