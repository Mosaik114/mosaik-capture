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
    static readonly Lazy<ScreenshotController> controller = new(() => new ScreenshotController(new WpfScreenshotUi(), new ScreenshotStore()));

    public static string Folder => CaptureSettings.ScreenshotFolder;

    public static void Start() => controller.Value.Start();

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

    /// <summary>
    /// <see cref="UiText.Files.ScreenshotPrefix"/> plus the time, e.g. "&lt;prefix&gt;2026-09-26 14-03-12.png";
    /// numbered " (2)", " (3)", ... if the name is taken.
    /// </summary>
    internal static string NewPath(string folder, DateTime when) =>
        UniqueFile.Next(Path.Combine(folder, UiText.Files.ScreenshotPrefix + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.CurrentCulture) + ".png"));
}
