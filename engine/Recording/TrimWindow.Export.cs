namespace MosaikCapture.Engine.Recording;

// Trim window: exporting the selection as MP4 or GIF.
internal sealed partial class TrimWindow
{
    // ---------- Export ----------

    void Export(bool gif)
    {
        if (exporting) return;
        Pause();
        bool whole = selectionStart <= 0.01 && selectionEnd >= duration - 0.01;
        if (!gif && whole && (withSound || !video!.Audio)) { Dismiss(); return; }   // nothing changed
        exporting = true;
        ShowProgressRow(true);
        string label = gif ? UiText.Recording.CreatingGif : UiText.Common.Saving;
        progressText.Text = label;
        progressText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        progressScale.ScaleX = 0;
        var work = workArea;
        exportCancellation = new CancellationTokenSource();
        VideoExport.Export(video!, selectionStart, selectionEnd, withSound, gif, fraction =>
        {
            progressScale.ScaleX = fraction;
            progressText.Text = UiText.Trim.Progress(label, fraction);
        }, (result, error) =>
        {
            exporting = false;
            exportCancellation?.Dispose();
            exportCancellation = null;
            if (result == null)
            {
                ShowExportError(error ?? UiText.Common.UnknownError);
                return;
            }
            exported = true;
            Dismiss();
            ScreenRecorder.Exported(result, gif, work);
        }, exportCancellation.Token);
    }

    /// <summary>Closing the window while exporting kills ffmpeg and removes the partial file.</summary>
    void CancelExport()
    {
        exportCancellation?.Cancel();
        exportCancellation?.Dispose();
        exportCancellation = null;
        exporting = false;
    }

    // The error stays for 4 s, then the buttons return.
    void ShowExportError(string error)
    {
        progressText.Text = UiText.Trim.NotSaved(error);
        progressText.SetResourceReference(TextBlock.ForegroundProperty, Palette.RecordingRed);
        ShowProgressRow(true);
        var timer = new DispatcherTimer { Interval = Motion.ErrorShown };
        timer.Tick += delegate { timer.Stop(); ShowProgressRow(false); };
        timer.Start();
    }

    void ShowProgressRow(bool progress)
    {
        actionsRow.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
        progressRow.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
    }
}
