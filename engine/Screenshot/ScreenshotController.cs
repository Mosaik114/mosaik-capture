namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// The screenshot flow: overlay → capture is saved and copied → card (edit, pin, text) → editor →
/// edited image replaces the file. Every entry point runs on the UI thread and is guarded: an
/// unexpected error closes the screenshot windows, tells the user and leaves the flow ready for the
/// next screenshot instead of half-open windows.
/// </summary>
internal sealed class ScreenshotController : IScreenshotFlow
{
    readonly IScreenshotUi ui;
    readonly IScreenshotStore store;
    ScreenCapture? last;   // last capture, kept while the card or the editor is open
    bool lastCopied;

    public ScreenshotController(IScreenshotUi ui, IScreenshotStore store)
    {
        this.ui = ui;
        this.store = store;
        ui.Bind(this);
    }

    /// <summary>The capture the card or editor works on, if any.</summary>
    public ScreenCapture? Current => last;

    public void Start() => Guard("Starting a screenshot", () =>
    {
        if (!ui.EditorOpen) ui.OpenOverlay();
    });

    public void Captured(ScreenCapture capture) => CapturedAsync(capture).LogFailures("Delivering a screenshot");

    /// <summary>Capture finished: save, copy to the clipboard, show the card.</summary>
    internal Task CapturedAsync(ScreenCapture capture) => GuardAsync("Delivering a screenshot", () =>
    {
        if (ui.EditorOpen) return Task.CompletedTask;
        capture.Path = store.NewPath(DateTime.Now);
        last = capture;
        return DeliverAsync(capture, UiText.Common.CopiedToClipboard);
    });

    public void Edit() => Guard("Opening the editor", () =>
    {
        if (last == null) return;
        ui.OpenEditor(last);
        ui.HideCard();
    });

    public void Pin() => Guard("Pinning a screenshot", () =>
    {
        if (last != null) ui.PinInPlace(last);
    });

    /// <summary>The card is gone: release the large frozen image (unless the editor still uses it).</summary>
    public void CardClosed()
    {
        if (!ui.EditorOpen) last = null;
    }

    public void Edited(ScreenCapture capture, BitmapSource image) => EditedAsync(capture, image).LogFailures("Delivering an edited screenshot");

    /// <summary>Done in the editor: the file is overwritten with the edited image.</summary>
    internal Task EditedAsync(ScreenCapture capture, BitmapSource image) => GuardAsync("Delivering an edited screenshot", () =>
    {
        capture.Image = image;
        last = capture;
        return DeliverAsync(capture, UiText.ScreenshotCard.EditedAndCopied);
    });

    public void EditedAndPinned(ScreenCapture capture, BitmapSource image) => EditedAndPinnedAsync(capture, image).LogFailures("Delivering an edited screenshot");

    internal Task EditedAndPinnedAsync(ScreenCapture capture, BitmapSource image) => GuardAsync("Pinning an edited screenshot", () =>
    {
        capture.Image = image;
        last = capture;
        var delivery = DeliverAsync(capture, UiText.ScreenshotCard.EditedAndCopied);   // shows the card right away
        ui.PinInPlace(capture);
        return delivery;
    });

    public void EditCancelled(ScreenCapture capture) => Guard("Closing the editor", () =>
    {
        last = capture;
        ui.ShowCard(capture, lastCopied ? UiText.Common.CopiedToClipboard : UiText.ScreenshotCard.SavedNotCopied);
        ui.CardSaved();
    });

    async Task DeliverAsync(ScreenCapture capture, string heading)
    {
        ui.ShowCard(capture, heading);
        byte[] png;
        try
        {
            png = await store.SaveAsync(capture.Image, capture.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ErrorLog.Write("Saving a screenshot", ex);
            ui.CardFailed();
            return;
        }
        lastCopied = await store.CopyAsync(capture.Image, png);
        if (lastCopied) ui.CardSaved();
        else ui.CardSavedNotCopied();
    }

    void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Recover(what, ex); }   // last line of defence for this flow: see Recover
    }

    async Task GuardAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Recover(what, ex); }
    }

    // Unexpected error: log it, close what is open and tell the user; the next screenshot starts clean.
    void Recover(string what, Exception error)
    {
        ErrorLog.Write(what, error);
        last = null;
        try
        {
            ui.CloseAll();
            ui.ShowProblem(UiText.ScreenshotCard.Problem, error.Message);
        }
        catch (Exception secondary)   // the windows themselves are broken: nothing more to show
        {
            ErrorLog.Write("Recovering from an error in the screenshot flow", secondary);
        }
    }
}
