using Microsoft.Win32;

namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Screenshots, as triggered by the host: freezes the screen and lets the user drag an area (or click
/// a window, Space = whole monitor); on release the image goes straight to the clipboard and is saved
/// as PNG to Pictures\Mosaiks-Screenshots. A card then offers editing, pinning and text recognition.
/// </summary>
internal static class Screenshots
{
    static SelectionOverlay? overlay;
    static ScreenshotCard? card;
    static ScreenshotEditor? editor;
    static ScreenCapture? last;   // last capture, kept while the card or the editor is open

    public static string Folder => CaptureSettings.ScreenshotFolder;

    static bool Editing => editor != null && editor.IsVisible;

    static ScreenshotCard Card => card ??= new ScreenshotCard(Edit, Pin, CardClosed);

    public static void Start()
    {
        if (Editing) return;
        overlay ??= new SelectionOverlay(Captured);
        overlay.Open();
    }

    /// <summary>Starts after a short delay so that whatever triggered it has left the screen.</summary>
    public static void StartSoon()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += delegate { timer.Stop(); Start(); };
        timer.Start();
    }

    // Capture finished: save, copy to the clipboard, show the card.
    static void Captured(ScreenCapture capture)
    {
        if (Editing) return;
        capture.Path = NewPath(Folder, DateTime.Now);
        last = capture;
        Deliver(capture, UiText.Common.CopiedToClipboard);
    }

    static void Deliver(ScreenCapture capture, string heading)
    {
        var shownCard = Card;
        BitmapSource image = capture.Image;
        shownCard.Present(capture, heading);
        string folder = Folder, path = capture.Path;
        Task.Run(() =>
        {
            byte[] png = Encode(image, false);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, png);
            return png;
        }).ContinueWith(saving =>
        {
            if (saving.IsFaulted)
            {
                ErrorLog.Write("Saving a screenshot", saving.Exception);
                shownCard.Failed();
                return;
            }
            ToClipboard(image, saving.Result);
            shownCard.Saved();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // "Bearbeiten" (edit) on the card.
    static void Edit()
    {
        if (last == null) return;
        editor ??= new ScreenshotEditor(Edited, EditCancelled, EditedAndPinned);
        editor.Open(last);
        Card.HideNow();
    }

    // "Anpinnen" (pin) on the card: the image floats where it was captured.
    static void Pin()
    {
        if (last != null) PinInPlace(last);
    }

    static void PinInPlace(ScreenCapture capture) =>
        PinnedScreenshot.Open(capture.Image, capture.VirtualLeft + capture.Crop.X, capture.VirtualTop + capture.Crop.Y, capture.Path);

    static void EditedAndPinned(ScreenCapture capture, BitmapSource image)
    {
        Edited(capture, image);
        PinInPlace(capture);
    }

    // Done in the editor: the file is overwritten with the edited image.
    static void Edited(ScreenCapture capture, BitmapSource image)
    {
        capture.Image = image;
        Deliver(capture, UiText.ScreenshotCard.EditedAndCopied);
    }

    static void EditCancelled(ScreenCapture capture)
    {
        Card.Present(capture, UiText.Common.CopiedToClipboard);
        Card.Saved();
    }

    // Card is gone: release the large frozen image.
    static void CardClosed()
    {
        if (!Editing) last = null;
    }

    public static byte[] Encode(BitmapSource image, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>"Save as" for the editor and pins: null = cancelled, "" = error, otherwise the file name.</summary>
    public static string? SaveAs(Window owner, BitmapSource image, string? suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Files.SaveAsTitle,
            Filter = UiText.Files.SaveAsFilter,
            FileName = Path.GetFileNameWithoutExtension(suggestedName ?? ""),
            InitialDirectory = Folder,
            DefaultExt = ".png",
            AddExtension = true
        };
        if (dialog.ShowDialog(owner) != true) return null;
        string extension = Path.GetExtension(dialog.FileName);
        bool jpeg = string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        if (jpeg) image = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);   // JPEG has no transparency
        try
        {
            File.WriteAllBytes(dialog.FileName, Encode(image, jpeg));
            return Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ErrorLog.Write("Saving a screenshot as " + dialog.FileName, ex);
            return "";
        }
    }

    /// <summary>"Mosaiks-Screenshot 2026-09-26 14-03-12.png"; numbered " (2)", " (3)", ... if the name is taken.</summary>
    internal static string NewPath(string folder, DateTime when) =>
        UniqueFile.Next(Path.Combine(folder, UiText.Files.ScreenshotPrefix + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.CurrentCulture) + ".png"));

    /// <summary>As a bitmap and additionally as PNG so browsers and chat apps paste it cleanly.</summary>
    public static void ToClipboard(BitmapSource image, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(png));
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) { Thread.Sleep(40); }   // clipboard briefly held by another app
        }
    }
}
