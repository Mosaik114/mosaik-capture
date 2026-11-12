namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Trim window: video preview, film strip with in/out handles, save as MP4 or GIF.
/// Keys: Space play/pause, I/O set in/out to the playhead, arrows 0.1 s (1 s with Shift),
/// Home/End jump, Enter saves, Esc closes.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The export cancellation is disposed when the export ends or the window is dismissed; the window lives as long as the engine.")]
internal sealed partial class TrimWindow : Window
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
