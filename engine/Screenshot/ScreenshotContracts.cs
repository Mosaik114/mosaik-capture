namespace MosaikCapture.Engine.Screenshot;

/// <summary>What the screenshot windows report back to <see cref="ScreenshotController"/> (all on the UI thread).</summary>
internal interface IScreenshotFlow
{
    /// <summary>The overlay finished: the capture with its crop.</summary>
    void Captured(ScreenCapture capture);
    /// <summary>The edit button on the card.</summary>
    void Edit();
    /// <summary>The pin button on the card.</summary>
    void Pin();
    /// <summary>The card faded out.</summary>
    void CardClosed();
    /// <summary>The editor is done; <paramref name="image"/> is the edited crop.</summary>
    void Edited(ScreenCapture capture, BitmapSource image);
    /// <summary>The editor is done and the result should also be pinned.</summary>
    void EditedAndPinned(ScreenCapture capture, BitmapSource image);
    /// <summary>The editor was closed without changes.</summary>
    void EditCancelled(ScreenCapture capture);
}

/// <summary>The windows of the screenshot flow, driven by <see cref="ScreenshotController"/>.</summary>
internal interface IScreenshotUi
{
    /// <summary>Connects the windows' callbacks to the flow; called once by the controller.</summary>
    void Bind(IScreenshotFlow flow);
    bool EditorOpen { get; }
    void OpenOverlay();
    void OpenEditor(ScreenCapture capture);
    void ShowCard(ScreenCapture capture, string heading);
    /// <summary>Saved and copied: the card offers the file actions.</summary>
    void CardSaved();
    /// <summary>Saved, but the clipboard refused it: the card must not claim it was copied.</summary>
    void CardSavedNotCopied();
    /// <summary>Not saved (folder not writable).</summary>
    void CardFailed();
    void HideCard();
    void PinInPlace(ScreenCapture capture);
    /// <summary>After an unexpected error: closes every screenshot window that is open.</summary>
    void CloseAll();
    void ShowProblem(string heading, string detail);
}

/// <summary>Files and clipboard of the screenshot flow.</summary>
internal interface IScreenshotStore
{
    /// <summary>A new, unused file name for a screenshot taken at <paramref name="when"/>.</summary>
    string NewPath(DateTime when);
    /// <summary>Encodes as PNG and writes the file (off the UI thread); returns the PNG bytes.</summary>
    Task<byte[]> SaveAsync(BitmapSource image, string path);
    /// <summary>Puts the image on the clipboard; false if another app held it too long.</summary>
    Task<bool> CopyAsync(BitmapSource image, byte[] png);
}
