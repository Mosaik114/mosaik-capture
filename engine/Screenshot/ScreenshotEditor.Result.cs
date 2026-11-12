namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: undo, keyboard and result (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    // ---------- Undo ----------

    void AddMarkup(FrameworkElement element)
    {
        // pixelated and blurred areas belong to the image: at the very bottom, below dimming and markup
        Action put = delegate
        {
            if (CoverTag.Equals(element.Tag)) markup.Children.Insert(0, element);
            else markup.Children.Add(element);
        };
        put();
        PushUndo(delegate { markup.Children.Remove(element); }, put);
        RefreshSpotlightShade(null);
    }

    void PushUndo(Action undoAction, Action redoAction) => history.Push(undoAction, redoAction);

    void Undo() => Replay(history.Undo);

    void Redo() => Replay(history.Redo);

    void Replay(Func<bool> step)
    {
        CommitText();
        if (!step()) return;
        RefreshSpotlightShade(null);
        UpdateNumberPreview();
    }

    void RemoveSelectedItem()
    {
        var element = selectedItem;
        if (element == null) return;
        int index = markup.Children.IndexOf(element);
        if (index < 0) return;
        selectedItem = null;
        markup.Children.RemoveAt(index);
        PushUndo(delegate { markup.Children.Insert(Math.Min(index, markup.Children.Count), element); }, delegate { markup.Children.Remove(element); });
        RefreshSpotlightShade(null);
    }

    // ---------- Keyboard ----------

    void OnKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0, shift = (modifiers & ModifierKeys.Shift) != 0;
        if (textBox != null)
        {
            if (key == Key.Escape || (key == Key.Enter && !shift)) { CommitText(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        if (key == Key.Escape && openMenuGroup != null) { CloseMenu(); return; }
        if (control && key == Key.Z && !shift) Undo();
        else if (control && (key == Key.Y || (key == Key.Z && shift))) Redo();
        else if (control && shift && key == Key.C) CopyText();
        else if (control && key == Key.C) CopyNow();
        else if (control && key == Key.S) SaveAs();
        else if (key == Key.Enter) Finish(pin: false);
        else if (key == Key.F3) Finish(pin: true);
        else if (key == Key.Escape) Cancel();
        else if (key is Key.Delete or Key.Back) RemoveSelectedItem();
        else if (!control && Array.IndexOf(ToolKeys, key) >= 0) SetTool((Tool)Array.IndexOf(ToolKeys, key));
        else if (key is Key.OemPlus or Key.Add or Key.OemCloseBrackets) StepWidth(1);
        else if (key is Key.OemMinus or Key.Subtract or Key.OemOpenBrackets) StepWidth(-1);
        else if (key >= Key.D1 && key <= Key.D6) SetColor(key - Key.D1);
        else if (key >= Key.NumPad1 && key <= Key.NumPad6) SetColor(key - Key.NumPad1);
        else e.Handled = false;
    }

    // ---------- Result ----------

    /// <summary>The crop including all markup at full resolution.</summary>
    internal BitmapSource Render()
    {
        Int32Rect pixels = CropInPixels();
        var result = new RenderTargetBitmap(pixels.Width, pixels.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawImage(new CroppedBitmap(capture.FullScreen, pixels), new Rect(0, 0, pixels.Width / scale, pixels.Height / scale));
        result.Render(background);
        if (markup.Children.Count > 0)
        {
            // shift the markup so the top-left corner of the crop is at 0,0
            Transform previous = markup.RenderTransform;
            markup.RenderTransform = new TranslateTransform(-pixels.X / scale, -pixels.Y / scale);
            markup.UpdateLayout();
            result.Render(markup);
            markup.RenderTransform = previous;
            markup.UpdateLayout();
        }
        result.Freeze();
        return result;
    }

    // Recognizes the text in the crop and copies it as text.
    void CopyText() => CopyTextAsync().OnFailure("Copying the text of a screenshot", () => ShowNotice(UiText.Common.TextRecognitionUnavailable));

    async Task CopyTextAsync()
    {
        CommitText();
        var image = new CroppedBitmap(capture.FullScreen, CropInPixels());
        image.Freeze();
        ShowNotice(UiText.Common.ReadingText);
        var result = await TextRecognition.ReadAsync(image);
        if (!IsVisible) return;
        if (result.Status != TextRecognitionStatus.Recognized)
        {
            ShowNotice(result.FailureMessage);
            return;
        }
        bool copied = await ClipboardWriter.SetTextAsync(result.Text);
        if (IsVisible) ShowNotice(copied ? UiText.Editor.TextCopied(TextRecognition.Summary(result.Text)) : UiText.Common.CopyFailed);
    }

    void CopyNow() => CopyNowAsync().OnFailure("Copying an edited screenshot", () => ShowNotice(UiText.Common.CopyFailed));

    async Task CopyNowAsync()
    {
        CommitText();
        var image = Render();
        bool copied = await ClipboardWriter.SetImageAsync(image, Screenshots.Encode(image, false));
        if (IsVisible) ShowNotice(copied ? UiText.Common.CopiedToClipboard : UiText.Common.CopyFailed);
    }

    void SaveAs()
    {
        CommitText();
        string? saved = Screenshots.SaveAs(this, Render(), capture.Path);
        if (saved != null) ShowNotice(saved.Length > 0 ? UiText.Editor.Saved(saved) : UiText.Common.CouldNotSave);
    }

    // Done: save and copy as usual; with pin, also pin the image in place.
    void Finish(bool pin)
    {
        CommitText();
        var image = Render();
        var edited = capture;
        edited.Crop = CropInPixels();
        HideAndReset();
        if (pin) onPinned(edited, image);
        else onDone(edited, image);
    }

    // Esc with changes asks once so nothing gets lost by accident.
    void Cancel()
    {
        CommitText();
        if (history.HasChanges && !discardArmed)
        {
            discardArmed = true;
            ShowNotice(UiText.Editor.ConfirmDiscard);
            return;
        }
        var unchanged = capture;
        HideAndReset();
        onCancelled(unchanged);
    }

    void HideAndReset()
    {
        NativeMethods.Cloak(hwnd, true);
        if (IsMouseCaptured) ReleaseMouseCapture();
        interaction.Reset();
        holdTimer.Stop();
        CloseMenu();
        Hide();
        markup.Children.Clear();
        history.Clear();
        frozenImage.Source = null;
        notice.Visibility = tooltip.Visibility = Visibility.Hidden;
        capture = null!;
    }
}
