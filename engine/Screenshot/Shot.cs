using Microsoft.Win32;

namespace MosaikCapture.Engine.Screenshot;

// Screenshot: freezes the screen, lets the user drag an area (or click a window,
// Space = whole monitor); on release the image goes straight to the clipboard and is saved as PNG
// to Pictures\Mosaiks-Screenshots. A card then appears in the bottom right.
// Triggered by the host (see CaptureBridge).
static class Shot
{
    static ShotOverlay? overlay;
    static ShotCard? card;
    static ShotEditor? editor;
    static ShotData? last;   // last capture, kept while the card or the editor is open

    public static string Folder => CaptureSettings.ScreenshotFolder;

    public static void Start()
    {
        if (editor != null && editor.IsVisible) return;
        if (overlay == null) overlay = new ShotOverlay(Done);
        overlay.Open();
    }

    // Starts after a short delay so that whatever triggered it has left the screen.
    public static void StartSoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += delegate { t.Stop(); Start(); };
        t.Start();
    }

    // Capture finished: save, copy to the clipboard, show the card.
    static void Done(ShotData d)
    {
        if (editor != null && editor.IsVisible) return;
        d.Path = NewPath();
        last = d;
        Deliver(d, "In Zwischenablage kopiert");
    }

    static void Deliver(ShotData d, string heading)
    {
        if (card == null) card = new ShotCard(Edit, Pin, Gone);
        BitmapSource img = d.Image;
        card.Present(d, heading);
        Task.Factory.StartNew(delegate
        {
            byte[] png = Encode(img, false);
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(d.Path, png);
            return png;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { ErrorLog.Write("Screenshot speichern", t.Exception); card!.Failed(); return; }
            ToClipboard(img, t.Result);
            card!.Saved();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // "Bearbeiten" (edit) on the card
    static void Edit()
    {
        if (last == null) return;
        if (editor == null) editor = new ShotEditor(Edited, EditCancelled, EditedPin);
        editor.Open(last);
        card!.HideNow();
    }

    // "Anpinnen" (pin) on the card: the image floats where it was captured
    static void Pin()
    {
        if (last != null) PinAt(last);
    }

    static void PinAt(ShotData d)
    {
        ShotPin.Open(d.Image, d.Vx + d.Cut.X, d.Vy + d.Cut.Y, d.Path);
    }

    static void EditedPin(ShotData d, BitmapSource img)
    {
        Edited(d, img);
        PinAt(d);
    }

    // Done in the editor: the file is overwritten with the edited image.
    static void Edited(ShotData d, BitmapSource img)
    {
        d.Image = img;
        Deliver(d, "Bearbeitet und kopiert");
    }

    static void EditCancelled(ShotData d)
    {
        card!.Present(d, "In Zwischenablage kopiert");
        card.Saved();
    }

    // Card is gone: release the large frozen image.
    static void Gone()
    {
        if (editor == null || !editor.IsVisible) last = null;
    }

    public static byte[] Encode(BitmapSource img, bool jpeg)
    {
        BitmapEncoder enc = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // "Save as" for the editor and pins: null = cancelled, "" = error, otherwise the file name.
    public static string? SaveAs(Window owner, BitmapSource img, string? suggested)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Mosaiks-Screenshot speichern unter",
            Filter = "PNG-Bild (*.png)|*.png|JPEG-Bild (*.jpg)|*.jpg",
            FileName = Path.GetFileNameWithoutExtension(suggested ?? ""),
            InitialDirectory = Folder,
            DefaultExt = ".png",
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return null;
        string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        bool jpg = ext == ".jpg" || ext == ".jpeg";
        if (jpg) img = new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);   // JPEG has no transparency
        try
        {
            File.WriteAllBytes(dlg.FileName, Encode(img, jpg));
            return Path.GetFileName(dlg.FileName);
        }
        catch (Exception) { return ""; }
    }

    static string NewPath() => NewPath(Folder, DateTime.Now);

    // "Mosaiks-Screenshot 2026-09-26 14-03-12.png"; numbered " (2)", " (3)", ... if the name is taken.
    internal static string NewPath(string folder, DateTime when)
    {
        string stamp = "Mosaiks-Screenshot " + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.CurrentCulture);
        string p = Path.Combine(folder, stamp + ".png");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(folder, stamp + " (" + i + ").png");
        return p;
    }

    // As a bitmap and additionally as PNG so browsers and chat apps paste it cleanly.
    public static void ToClipboard(BitmapSource img, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(img);
        data.SetData("PNG", new MemoryStream(png));
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) { Thread.Sleep(40); }   // clipboard briefly held by another app
        }
    }
}
