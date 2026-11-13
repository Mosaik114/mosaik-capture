namespace MosaikCapture.Engine.Screenshot;

/// <summary>The real screenshot windows. Each window is created on first use.</summary>
internal sealed class WpfScreenshotUi : IScreenshotUi
{
    IScreenshotFlow? flow;
    SelectionOverlay? overlay;
    ScreenshotCard? card;
    ScreenshotEditor? editor;

    IScreenshotFlow Flow => flow ?? throw new InvalidOperationException("The screenshot windows are not bound to a flow.");
    ScreenshotCard Card => card ??= new ScreenshotCard(() => Flow.Edit(), () => Flow.Pin(), () => Flow.CardClosed());
    ScreenshotEditor Editor => editor ??= new ScreenshotEditor((c, i) => Flow.Edited(c, i), c => Flow.EditCancelled(c), (c, i) => Flow.EditedAndPinned(c, i));

    public void Bind(IScreenshotFlow screenshotFlow) => flow = screenshotFlow;

    public bool EditorOpen => editor != null && editor.IsVisible;

    public void OpenOverlay()
    {
        overlay ??= new SelectionOverlay(c => Flow.Captured(c));
        overlay.Open();
    }

    public void OpenEditor(ScreenCapture capture) => Editor.Open(capture);
    public void ShowCard(ScreenCapture capture, string heading) => Card.Present(capture, heading);
    public void CardSaved() => Card.Saved();
    public void CardSavedNotCopied() => Card.SavedNotCopied();
    public void CardFailed() => Card.Failed();
    public void HideCard() => Card.HideNow();

    public void PinInPlace(ScreenCapture capture) =>
        PinnedScreenshot.Open(capture.Image, capture.VirtualLeft + capture.Crop.X, capture.VirtualTop + capture.Crop.Y, capture.Path);

    public void CloseAll()
    {
        overlay?.Cancel();
        editor?.Abandon();
        card?.HideNow();
    }

    public void ShowProblem(string heading, string detail) => Card.ShowProblem(heading, detail, NativeMethods.WorkAreaAtCursor());
}

/// <summary>Screenshot files below the configured folder and the clipboard.</summary>
internal sealed class ScreenshotStore : IScreenshotStore
{
    public string NewPath(DateTime when) => Screenshots.NewPath(Screenshots.ReadFolder(), when);

    public Task<byte[]> SaveAsync(BitmapSource image, string path) => Task.Run(() =>
    {
        byte[] png = Screenshots.Encode(image, false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, png);
        return png;
    });

    public Task<bool> CopyAsync(BitmapSource image, byte[] png) => ClipboardWriter.SetImageAsync(image, png);
}
