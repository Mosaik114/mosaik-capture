using Microsoft.Win32;

namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Screenshots, as triggered by the host: freezes the screen and lets the user drag an area (or click
/// a window, Space = whole monitor); on release the image goes straight to the clipboard and is saved
/// as PNG to the screenshot folder. A card then offers editing, pinning and text recognition.
/// This is a thin static facade over one <see cref="ScreenshotController"/> with the real windows.
/// </summary>
internal static class Screenshots
{
    static ScreenshotController? controller;
    static volatile string? folder;

    /// <summary>Called once by <see cref="EngineComposition"/>, which builds the object graph.</summary>
    internal static void Use(ScreenshotController screenshotController) => controller = screenshotController;

    /// <summary>
    /// The screenshot folder of the latest capture: the settings are read once per capture
    /// (<see cref="ReadFolder"/>), not on every use by the card, the editor or a pin.
    /// </summary>
    public static string Folder => folder ?? ReadFolder();

    /// <summary>Reads the folder from the settings again (a new capture is about to be saved).</summary>
    internal static string ReadFolder() => folder = CaptureSettings.Load().ScreenshotFolder;

    public static void Start() => (controller ?? throw EngineComposition.NotComposed()).Start();

    /// <summary>Starts after a short delay so that whatever triggered it has left the screen.</summary>
    public static void StartSoon()
    {
        var timer = new DispatcherTimer { Interval = Motion.TriggerDelay };
        timer.Tick += delegate { timer.Stop(); Start(); };
        timer.Start();
    }

    public static byte[] Encode(BitmapSource image, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>"Save as" for the editor and pins; <paramref name="fileName"/> is the chosen file's name (empty if cancelled).</summary>
    public static SaveAsResult SaveAs(Window owner, BitmapSource image, string? suggestedName, out string fileName)
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
        fileName = "";
        if (dialog.ShowDialog(owner) != true) return SaveAsResult.Cancelled;
        fileName = Path.GetFileName(dialog.FileName);
        return Write(image, dialog.FileName);
    }

    /// <summary>Writes PNG, or JPEG for a .jpg/.jpeg name (without transparency); failures are logged.</summary>
    internal static SaveAsResult Write(BitmapSource image, string file)
    {
        string extension = Path.GetExtension(file);
        bool jpeg = string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        if (jpeg) image = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);   // JPEG has no transparency
        try
        {
            File.WriteAllBytes(file, Encode(image, jpeg));
            return SaveAsResult.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ErrorLog.Write("Saving a screenshot as " + file, ex);
            return SaveAsResult.Failed;
        }
    }

    /// <summary>
    /// <see cref="UiText.Files.ScreenshotPrefix"/> plus the time, e.g. "&lt;prefix&gt;2026-09-26 14-03-12.png";
    /// numbered " (2)", " (3)", ... if the name is taken.
    /// </summary>
    internal static string NewPath(string folder, DateTime when) =>
        UniqueFile.Next(Path.Combine(folder, UiText.Files.ScreenshotPrefix + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".png"));
}

/// <summary>How "save as" ended.</summary>
internal enum SaveAsResult
{
    Cancelled,
    Saved,
    /// <summary>The file could not be written (logged).</summary>
    Failed,
}
