namespace MosaikCapture.Engine.Recording;

// Trim window: film strip, selection handles and playhead.
internal sealed partial class TrimWindow
{
    // As many frames as fit side by side, rendered by ffmpeg without blocking a thread; closing or
    // reopening the window cancels a strip that is still being made.
    void LoadFilmStrip(RecordedVideo recorded, int openedAt)
    {
        double aspect = recorded.Width > 0 && recorded.Height > 0 ? (double)recorded.Width / recorded.Height : 16.0 / 9;
        int frames = Math.Max(2, Math.Min(24, (int)Math.Ceiling(StripWidth / (StripHeight * aspect))));
        CancelFilmStrip();
        stripCancellation = new CancellationTokenSource();
        ShowFilmStripAsync(recorded.Path, duration, frames, openedAt, stripCancellation.Token).LogFailures("Rendering the film strip");
    }

    async Task ShowFilmStripAsync(string path, double length, int frames, int openedAt, CancellationToken cancellation)
    {
        var image = await RecordedVideo.StripAsync(path, length, frames, (int)StripHeight * 2, cancellation);   // back on the UI thread
        if (openedAt == generation && !cancellation.IsCancellationRequested) strip.Source = image;
    }

    void CancelFilmStrip()
    {
        stripCancellation?.Cancel();
        stripCancellation?.Dispose();
        stripCancellation = null;
    }

    // ---------- Timeline ----------

    double TimeToX(double seconds) => HandleWidth + seconds / duration * StripWidth;

    double XToTime(double x) => Math.Max(0, Math.Min(duration, (x - HandleWidth) / StripWidth * duration));

    /// <summary>Tests: sets selection and playhead without a video player.</summary>
    internal void ShowSelection(double start, double end, double position)
    {
        selectionStart = start;
        selectionEnd = end;
        playhead = position;
        Draw();
    }

    void Draw()
    {
        double startX = Math.Round(TimeToX(selectionStart)), endX = Math.Round(TimeToX(selectionEnd)), playheadX = Math.Round(TimeToX(playhead));
        dimBefore.Width = Math.Max(0, startX - HandleWidth);
        Canvas.SetLeft(dimBefore, HandleWidth);
        dimAfter.Width = Math.Max(0, ContentWidth - HandleWidth - endX);
        Canvas.SetLeft(dimAfter, endX);
        Canvas.SetLeft(startHandle, startX - HandleWidth);
        Canvas.SetLeft(endHandle, endX);
        selectionTop.Width = selectionBottom.Width = Math.Max(0, endX - startX);
        Canvas.SetLeft(selectionTop, startX);
        Canvas.SetLeft(selectionBottom, startX);
        Canvas.SetLeft(playheadLine, playheadX - 1);
        Canvas.SetLeft(playheadKnob, playheadX - 6);
        timeText.Text = Formats.ClockWithTenths(playhead) + " / " + Formats.ClockWithTenths(duration);
        selectionText.Text = UiText.Trim.Selection(Formats.ClockWithTenths(selectionEnd - selectionStart));
    }

    void Refresh()
    {
        playGlyph.Text = playing ? IconGlyphs.Pause : IconGlyphs.PlaySolid;
        playGlyph.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0);
        bigPlayButton.Visibility = playing ? Visibility.Hidden : Visibility.Visible;
        soundGlyph.Text = withSound ? IconGlyphs.Volume : IconGlyphs.Mute;
        if (withSound) soundGlyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        else soundGlyph.Foreground = Palette.RecordingRed;
    }

    void TrackDown(double x)
    {
        if (exporting) return;
        double startX = TimeToX(selectionStart), endX = TimeToX(selectionEnd);
        if (x >= startX - HandleWidth - 3 && x <= startX + 3) { dragging = DragTarget.Start; grabOffset = startX - x; }
        else if (x >= endX - 3 && x <= endX + HandleWidth + 3) { dragging = DragTarget.End; grabOffset = endX - x; }
        else { dragging = DragTarget.Playhead; grabOffset = 0; }
        track.CaptureMouse();
        TrackMove(x);
    }

    // Dragging a handle shows the frame at that position; otherwise the playhead follows.
    void TrackMove(double x)
    {
        double time = XToTime(x + grabOffset);
        switch (dragging)
        {
            case DragTarget.Start:
                selectionStart = Math.Max(0, Math.Min(time, selectionEnd - MinSelection));
                Pause();
                Seek(selectionStart);
                break;
            case DragTarget.End:
                selectionEnd = Math.Min(duration, Math.Max(time, selectionStart + MinSelection));
                Pause();
                Seek(selectionEnd);
                break;
            case DragTarget.Playhead:
                Pause();
                Seek(time);
                break;
        }
    }
}
