using System.Windows.Documents;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Trim window: video preview, film strip with in/out handles, save as MP4 or GIF.
/// Keys: Space play/pause, I/O set in/out to the playhead, arrows 0.1 s (1 s with Shift),
/// Home/End jump, Enter saves, Esc closes.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The export cancellation is disposed when the export ends or the window is dismissed; the window lives as long as the engine.")]
internal sealed class TrimWindow : Window
{
    const double ContentWidth = 880, HandleWidth = 14, TrackHeight = 64, StripTop = 4, StripHeight = 56;
    const double MinSelection = 0.3;   // seconds
    const double StripWidth = ContentWidth - 2 * HandleWidth;

    enum DragTarget { None, Start, End, Playhead }

    readonly IntPtr hwnd;
    readonly Border videoBox, bigPlayButton, playButton, soundButton, actionsRow, progressRow;
    readonly Border startHandle, endHandle, selectionTop, selectionBottom, playheadKnob;
    readonly MediaElement media;
    readonly Image poster, strip;
    readonly TextBlock subtitle, timeText, selectionText, playGlyph, soundGlyph, progressText;
    readonly Canvas track;
    readonly Shapes.Rectangle dimBefore, dimAfter, playheadLine;
    readonly ScaleTransform progressScale = new(0, 1);
    /// <summary>How often the playhead follows the playing video.</summary>
    static readonly TimeSpan PlaybackTick = TimeSpan.FromMilliseconds(30);
    /// <summary>Darkens the film strip outside the selection.</summary>
    static readonly SolidColorBrush StripShade = Palette.Frozen(0xB8, 0x14, 0x14, 0x13);

    readonly DispatcherTimer playbackTimer = new(DispatcherPriority.Render) { Interval = PlaybackTick };
    RecordedVideo? video;
    RECT workArea;
    double duration = 1, selectionStart, selectionEnd = 1, playhead, grabOffset;
    bool playing, withSound, exporting, exported;
    CancellationTokenSource? exportCancellation;
    DragTarget dragging;
    int generation;

    public TrimWindow()
    {
        ConfigureWindow();

        subtitle = ToolWindows.Text(12, Palette.Muted);
        poster = new Image { Stretch = Stretch.Uniform };
        media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
        bigPlayButton = BuildBigPlayButton();
        videoBox = BuildVideo();

        playGlyph = ToolWindows.Glyph(IconGlyphs.PlaySolid, 14);
        playButton = PlainButton(playGlyph, 36, 18);
        timeText = ToolWindows.Text(13, Palette.Text);
        selectionText = ToolWindows.Text(13, Palette.Muted);

        track = new Canvas { Width = ContentWidth, Height = TrackHeight, Margin = new Thickness(0, 16, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        strip = new Image { Width = StripWidth, Height = StripHeight, Stretch = Stretch.UniformToFill, ClipToBounds = true };   // do not squash, crop the overflow
        dimBefore = new Shapes.Rectangle { Height = StripHeight, Fill = StripShade };
        dimAfter = new Shapes.Rectangle { Height = StripHeight, Fill = StripShade };
        selectionTop = SelectionEdge(0);
        selectionBottom = SelectionEdge(TrackHeight - StripTop);
        startHandle = TrimHandle(new CornerRadius(5, 0, 0, 5));
        endHandle = TrimHandle(new CornerRadius(0, 5, 5, 0));
        playheadLine = new Shapes.Rectangle { Width = 2, Height = TrackHeight + 12, IsHitTestVisible = false };
        playheadKnob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), IsHitTestVisible = false, BorderThickness = new Thickness(2) };
        BuildTimeline();

        soundGlyph = ToolWindows.Glyph("", 14);
        soundButton = PillButton(soundGlyph, UiText.Trim.Sound, false);
        actionsRow = BuildActions();
        progressText = ToolWindows.Text(12.5, Palette.Text);
        progressRow = BuildProgress();

        Content = BuildCard();
        PreviewKeyDown += (_, e) => OnKey(e);
        hwnd = ToolWindows.Handle(this, 0);
        playbackTimer.Tick += delegate { FollowPlayback(); };
    }

    // A normal window (taskbar, activation) unlike the other recording windows: the user works in it.
    void ConfigureWindow()
    {
        ToolWindows.Setup(this, UiText.Titles.Trim);
        ShowActivated = true;
        Topmost = false;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
    }

    Border BuildCard()
    {
        var layout = new StackPanel { Width = ContentWidth };
        layout.Children.Add(BuildHeader());
        layout.Children.Add(videoBox);
        layout.Children.Add(BuildTransport());
        layout.Children.Add(track);
        layout.Children.Add(BuildFooter());
        var card = new Border
        {
            Margin = new Thickness(24),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = layout,
            Effect = ToolWindows.Shadow(32, 10, 0.4)
        };
        card.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        card.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return card;
    }

    /// <summary>Tests: build the content but never show the window.</summary>
    internal bool Offscreen { get; set; }

    /// <summary>Tests: the film strip arrived from ffmpeg.</summary>
    internal bool FilmStripLoaded => strip.Source != null;

    // ---------- Building ----------

    // Title, file and close button; drag here to move the window.
    DockPanel BuildHeader()
    {
        var title = new TextBlock { FontFamily = Palette.TitleFont, FontSize = 18, Text = UiText.Titles.Trim };
        title.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        subtitle.Margin = new Thickness(0, 3, 0, 0);
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(subtitle);
        var close = PlainButton(ToolWindows.Glyph(IconGlyphs.ChromeClose, 11), 32, 16);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.MouseLeftButtonUp += delegate { Dismiss(); };
        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(texts);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }   // mouse button already released
        };
        return header;
    }

    static Border BuildBigPlayButton()
    {
        var glyph = ToolWindows.Glyph(IconGlyphs.PlaySolid, 22);
        glyph.Foreground = Palette.Light;
        glyph.Margin = new Thickness(4, 0, 0, 0);
        return new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(30),
            Child = glyph,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x13))
        };
    }

    // The video with a poster until it has loaded; click = play/pause.
    Border BuildVideo()
    {
        media.MediaOpened += delegate { poster.Visibility = Visibility.Hidden; Seek(playhead); };
        media.MediaEnded += delegate { Pause(); Seek(selectionEnd); };
        var layers = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        layers.Children.Add(poster);
        layers.Children.Add(media);
        layers.Children.Add(bigPlayButton);
        layers.MouseLeftButtonUp += delegate { TogglePlay(); };
        return new Border { Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(10), Child = layers, Background = Palette.Frozen(0xFF, 0x14, 0x14, 0x13) };
    }

    // Play button, time and length of the selection.
    DockPanel BuildTransport()
    {
        playButton.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        playButton.MouseLeftButtonUp += delegate { TogglePlay(); };
        timeText.Margin = new Thickness(12, 0, 0, 0);
        Typography.SetNumeralAlignment(timeText, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(selectionText, FontNumeralAlignment.Tabular);
        var transport = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(selectionText, Dock.Right);
        transport.Children.Add(selectionText);
        transport.Children.Add(playButton);
        transport.Children.Add(timeText);
        return transport;
    }

    // Film strip, dimmed outside the selection, handles and playhead (in this z-order).
    void BuildTimeline()
    {
        var placeholder = new Shapes.Rectangle { Width = StripWidth, Height = StripHeight };
        placeholder.SetResourceReference(Shapes.Shape.FillProperty, Palette.Hover);
        AddToTrack(placeholder, HandleWidth, StripTop);
        AddToTrack(strip, HandleWidth, StripTop);
        AddToTrack(dimBefore, null, StripTop);
        AddToTrack(dimAfter, null, StripTop);
        track.Children.Add(selectionTop);
        track.Children.Add(selectionBottom);
        track.Children.Add(startHandle);
        track.Children.Add(endHandle);
        playheadLine.SetResourceReference(Shapes.Shape.FillProperty, Palette.Text);
        AddToTrack(playheadLine, null, -6);
        playheadKnob.SetResourceReference(Border.BackgroundProperty, Palette.Text);
        playheadKnob.SetResourceReference(Border.BorderBrushProperty, Palette.Background);
        AddToTrack(playheadKnob, null, -12);
        track.MouseLeftButtonDown += (_, e) => TrackDown(e.GetPosition(track).X);
        track.MouseMove += (_, e) => { if (dragging != DragTarget.None) TrackMove(e.GetPosition(track).X); };
        track.MouseLeftButtonUp += delegate { dragging = DragTarget.None; track.ReleaseMouseCapture(); };
    }

    void AddToTrack(UIElement element, double? left, double top)
    {
        if (left.HasValue) Canvas.SetLeft(element, left.Value);
        Canvas.SetTop(element, top);
        track.Children.Add(element);
    }

    // Key hints and the buttons for sound, GIF and save.
    Border BuildActions()
    {
        var hint = ToolWindows.Text(11.5, Palette.Muted);
        hint.Text = UiText.Trim.KeyHint;
        soundButton.MouseLeftButtonUp += delegate { withSound = !withSound; media.IsMuted = !withSound; Refresh(); };
        var gifButton = PillButton(ToolWindows.Glyph(IconGlyphs.Gif, 14), UiText.Trim.AsGif, false);
        gifButton.MouseLeftButtonUp += delegate { Export(gif: true); };
        var saveButton = PillButton(ToolWindows.Glyph(IconGlyphs.Save, 14), UiText.Trim.Save, true);
        saveButton.MouseLeftButtonUp += delegate { Export(gif: false); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(soundButton);
        buttons.Children.Add(gifButton);
        buttons.Children.Add(saveButton);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(hint);
        return new Border { Child = row };
    }

    // Shown instead of the actions while exporting.
    Border BuildProgress()
    {
        var rail = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
        rail.SetResourceReference(Border.BackgroundProperty, Palette.Line);
        var fill = new Border { CornerRadius = new CornerRadius(2), RenderTransform = progressScale };
        fill.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        rail.Child = fill;
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(progressText);
        content.Children.Add(rail);
        return new Border { Child = content, Visibility = Visibility.Collapsed };
    }

    Grid BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0), MinHeight = 34 };
        footer.Children.Add(actionsRow);
        footer.Children.Add(progressRow);
        return footer;
    }

    Border PlainButton(TextBlock glyph, double size, double radius)
    {
        glyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var button = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = glyph, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        button.MouseEnter += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.Selected); };
        button.MouseLeave += delegate
        {
            if (button == playButton) button.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
            else button.Background = Brushes.Transparent;
        };
        return button;
    }

    // Button with icon and label; the primary button uses the accent color.
    static Border PillButton(TextBlock glyph, string label, bool primary)
    {
        var text = new TextBlock { Text = label, FontSize = 12.5, FontFamily = Palette.UiFont, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (primary)
        {
            text.FontWeight = FontWeights.SemiBold;
            text.Foreground = glyph.Foreground = Palette.Light;
        }
        else
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
            if (glyph.Text.Length > 0) glyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(text);
        var button = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0, 14, 0), Height = 34, Margin = new Thickness(8, 0, 0, 0), Child = content, Cursor = Cursors.Hand };
        string background = primary ? Palette.Accent : Palette.Hover;
        button.SetResourceReference(Border.BackgroundProperty, background);
        if (primary)
        {
            button.MouseEnter += delegate { button.Opacity = 0.88; };
            button.MouseLeave += delegate { button.Opacity = 1; };
        }
        else
        {
            button.MouseEnter += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.Selected); };
            button.MouseLeave += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.Hover); };
        }
        return button;
    }

    static Border SelectionEdge(double top)
    {
        var edge = new Border { Height = StripTop, IsHitTestVisible = false };
        edge.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        Canvas.SetTop(edge, top);
        return edge;
    }

    static Border TrimHandle(CornerRadius corners)
    {
        var grip = new Border
        {
            Width = 2,
            Height = 20,
            CornerRadius = new CornerRadius(1),
            Background = Palette.Frozen(0xCC, 0xFA, 0xF9, 0xF5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var handle = new Border { Width = HandleWidth, Height = TrackHeight, CornerRadius = corners, Child = grip, Cursor = Cursors.SizeWE };
        handle.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        return handle;
    }

    // ---------- Opening ----------

    public void Open(RecordedVideo recorded, RECT work)
    {
        video = recorded;
        workArea = work;
        duration = Math.Max(0.1, recorded.Duration);
        selectionStart = 0;
        selectionEnd = duration;
        playhead = 0;
        withSound = recorded.Audio;
        exporting = exported = false;
        playing = false;
        int openedAt = ++generation;
        Palette.Apply(Resources);
        subtitle.Text = UiText.Trim.VideoInfo(Path.GetFileName(recorded.Path), Formats.Clock(Math.Round(recorded.Duration)), recorded.Width, recorded.Height);
        double videoHeight = recorded.Width > 0 ? ContentWidth * recorded.Height / recorded.Width : ContentWidth * 9 / 16;
        videoBox.Height = Math.Round(Math.Max(180, Math.Min(495, videoHeight)));
        videoBox.Width = ContentWidth;
        videoBox.Clip = new RectangleGeometry(new Rect(0, 0, ContentWidth, videoBox.Height), 10, 10);
        poster.Source = recorded.Thumbnail;
        poster.Visibility = Visibility.Visible;
        media.IsMuted = !withSound;
        if (!Offscreen)
        {
            media.Source = new Uri(recorded.Path);
            media.Play();
            media.Pause();
        }
        soundButton.Visibility = recorded.Audio ? Visibility.Visible : Visibility.Collapsed;
        actionsRow.Visibility = Visibility.Visible;
        progressRow.Visibility = Visibility.Collapsed;
        strip.Source = null;
        Refresh();
        Draw();
        LoadFilmStrip(recorded, openedAt);

        if (Offscreen) return;
        Left = -10000;
        Show();
        UpdateLayout();
        double scale = ToolWindows.Scale(hwnd);
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, (work.left + work.right - width) / 2, Math.Max(work.top, (work.top + work.bottom - height) / 2), 0, 0, NativeMethods.SWP_NOSIZE);
        ToolWindows.TakeFocus(this, hwnd);
    }

    // As many frames as fit side by side, rendered in the background.
    void LoadFilmStrip(RecordedVideo recorded, int openedAt)
    {
        double aspect = recorded.Width > 0 && recorded.Height > 0 ? (double)recorded.Width / recorded.Height : 16.0 / 9;
        int frames = Math.Max(2, Math.Min(24, (int)Math.Ceiling(StripWidth / (StripHeight * aspect))));
        string path = recorded.Path;
        double length = duration;
        Task.Run(() => RecordedVideo.Strip(path, length, frames, (int)StripHeight * 2)).ContinueWith(task =>
        {
            if (task.IsFaulted) ErrorLog.Write("Rendering the film strip", task.Exception);
            else if (openedAt == generation) strip.Source = task.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
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
        progressText.Foreground = Palette.RecordingRed;
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

    void Dismiss()
    {
        if (exporting) CancelExport();
        generation++;
        playbackTimer.Stop();
        playing = false;
        media.Stop();
        media.Close();
        media.Source = null;
        Hide();
        var closed = video;
        video = null;
        if (!exported && closed != null) ScreenRecorder.TrimWindowClosed(closed, workArea);
    }
}
