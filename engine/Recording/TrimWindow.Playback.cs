namespace MosaikCapture.Engine.Recording;

// Trim window: video playback and keyboard.
internal sealed partial class TrimWindow
{
    // ---------- Playback ----------

    void Seek(double seconds)
    {
        playhead = Math.Max(0, Math.Min(duration, seconds));
        media.Position = TimeSpan.FromSeconds(playhead);
        Draw();
    }

    void TogglePlay()
    {
        if (exporting) return;
        if (playing) Pause(); else Play();
    }

    void Play()
    {
        if (playhead >= selectionEnd - 0.05 || playhead < selectionStart) Seek(selectionStart);
        media.Play();
        playing = true;
        playbackTimer.Start();
        Refresh();
    }

    void Pause()
    {
        if (!playing) return;
        media.Pause();
        playing = false;
        playbackTimer.Stop();
        playhead = Math.Max(0, Math.Min(duration, media.Position.TotalSeconds));
        Refresh();
        Draw();
    }

    // While playing: move the playhead and stop at the end of the selection.
    void FollowPlayback()
    {
        playhead = media.Position.TotalSeconds;
        if (playhead >= selectionEnd) { Pause(); Seek(selectionEnd); return; }
        Draw();
    }

    void OnKey(KeyEventArgs e)
    {
        if (exporting && e.Key != Key.Escape) { e.Handled = true; return; }   // Esc cancels the export
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 0.1;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: Pause(); selectionStart = Math.Max(0, Math.Min(playhead, selectionEnd - MinSelection)); Draw(); break;
            case Key.O: Pause(); selectionEnd = Math.Min(duration, Math.Max(playhead, selectionStart + MinSelection)); Draw(); break;
            case Key.Left: Pause(); Seek(playhead - step); break;
            case Key.Right: Pause(); Seek(playhead + step); break;
            case Key.Home: Pause(); Seek(selectionStart); break;
            case Key.End: Pause(); Seek(selectionEnd); break;
            case Key.Enter: Export(gif: false); break;
            case Key.Escape: Dismiss(); break;
            default: return;
        }
        e.Handled = true;
    }
}
