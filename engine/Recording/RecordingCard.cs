using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Card in the bottom right after a recording (like the screenshot card, see <see cref="CardChrome"/>):
/// thumbnail, file name and size, trim / GIF / folder / delete. Also shows "saving…", GIF progress and errors.
/// </summary>
internal sealed class RecordingCard : Window
{
    const int LifetimeMs = 8000;
    readonly IntPtr hwnd;
    readonly Border card, thumbnailBox, lengthChip, playChip, statusChip;
    readonly Image thumbnail;
    readonly TextBlock title, subtitle, lengthText, statusGlyph, placeholderGlyph;
    readonly UniformGrid buttons;
    readonly ScaleTransform barScale = new(1, 1);
    readonly TranslateTransform barMove = new();
    readonly TranslateTransform slide = new();
    RecordedVideo? video;
    RECT workArea;
    bool ready, pressing, counting;
    int generation;   // invalidates pending timers and animations of an earlier state
    Point pressedAt;

    public RecordingCard()
    {
        ToolWindows.Setup(this, UiText.Titles.RecordingCard);
        SizeToContent = SizeToContent.WidthAndHeight;

        thumbnail = CardChrome.Thumbnail();
        playChip = BuildPlayChip();
        lengthText = new TextBlock { FontSize = 11, FontFamily = Palette.UiFont, Foreground = Palette.Light };
        lengthChip = BuildLengthChip(lengthText);
        placeholderGlyph = ToolWindows.Glyph(IconGlyphs.Video, 26);
        placeholderGlyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        thumbnailBox = BuildThumbnailBox();

        statusGlyph = ToolWindows.Glyph(IconGlyphs.Accept, 11);
        statusChip = CardChrome.StatusChip(statusGlyph);
        title = CardChrome.Title();
        subtitle = CardChrome.Subtitle();
        buttons = CardChrome.ButtonRow();
        var transforms = new TransformGroup();
        transforms.Children.Add(barScale);
        transforms.Children.Add(barMove);

        var layout = new StackPanel();
        layout.Children.Add(thumbnailBox);
        layout.Children.Add(CardChrome.Header(statusChip, title, subtitle));
        layout.Children.Add(buttons);
        var track = CardChrome.BarTrack(transforms);   // life time countdown, indeterminate "saving" bar or GIF progress
        track.ClipToBounds = true;                     // the indeterminate bar sweeps beyond both ends
        layout.Children.Add(track);
        card = CardChrome.Frame(layout, slide);
        Content = card;

        HookUpMouse();
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>Tests: build the content but never show the window.</summary>
    internal bool Offscreen { get; set; }

    // ---------- Building ----------

    static Border BuildPlayChip()
    {
        var play = ToolWindows.Glyph(IconGlyphs.PlaySolid, 16);
        play.Foreground = Palette.Light;
        play.Margin = new Thickness(3, 0, 0, 0);   // optically center the triangle
        return new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Child = play,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x13)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    static Border BuildLengthChip(TextBlock text)
    {
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = text,
            Background = new SolidColorBrush(Palette.ImageChip.Color),
            IsHitTestVisible = false
        };
    }

    Border BuildThumbnailBox()
    {
        var layers = new Grid();
        layers.Children.Add(placeholderGlyph);
        layers.Children.Add(thumbnail);
        layers.Children.Add(playChip);
        layers.Children.Add(lengthChip);
        return CardChrome.ThumbnailBox(layers);
    }

    void HookUpMouse()
    {
        // The card stays while the mouse is over it
        card.MouseEnter += delegate { if (counting) { barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); barScale.ScaleX = 1; } };
        card.MouseLeave += delegate { if (IsVisible && !pressing && counting) StartLifetime(); };
        card.MouseRightButtonUp += delegate { if (ready || counting) FadeOut(); };

        thumbnail.MouseLeftButtonDown += (_, e) => { pressedAt = e.GetPosition(this); pressing = true; thumbnail.CaptureMouse(); e.Handled = true; };
        thumbnail.MouseMove += (_, e) =>
        {
            if (!pressing || !ready) return;
            Vector moved = e.GetPosition(this) - pressedAt;
            if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6) return;
            pressing = false;
            thumbnail.ReleaseMouseCapture();
            DragDrop.DoDragDrop(thumbnail, new DataObject(DataFormats.FileDrop, new[] { video!.Path }), DragDropEffects.Copy);   // drag into chat, mail, Explorer ...
            if (!card.IsMouseOver && counting) StartLifetime();
        };
        thumbnail.MouseLeftButtonUp += (_, _) =>
        {
            if (!pressing) return;
            pressing = false;
            thumbnail.ReleaseMouseCapture();
            if (ready) { Shell.Open(video!.Path); FadeOut(); }
        };
    }

    // Only while the buttons are shown (not during a GIF export or after deleting).
    Border ActionButton(string glyph, string label, Action click) =>
        CardChrome.ActionButton(glyph, label, delegate { if (buttons.Visibility == Visibility.Visible) click(); });

    // ---------- States ----------

    /// <summary>While ffmpeg assembles the video: placeholder and indeterminate bar.</summary>
    public void Saving(double seconds, RECT work)
    {
        ShowPlaceholder(work, UiText.Common.Saving, UiText.Recording.Recorded(Formats.Clock(seconds)), IconGlyphs.Accept);
        statusChip.Visibility = Visibility.Collapsed;
        StartIndeterminateBar();
        Place();
    }

    public void Problem(string heading, string reason, RECT work)
    {
        ShowPlaceholder(work, heading, reason, IconGlyphs.Warning);
        statusChip.Visibility = Visibility.Visible;
        Place();
        StartLifetime();
    }

    /// <summary>Saving failed: the reason, and the kept raw data to open (if any) so the recording can be rescued.</summary>
    public void SaveFailed(string reason, string? rawDataFolder, RECT work)
    {
        ShowPlaceholder(work, UiText.Recording.Failed, reason, IconGlyphs.Warning);
        statusChip.Visibility = Visibility.Visible;
        if (rawDataFolder != null)
        {
            buttons.Children.Clear();
            buttons.Children.Add(ActionButton(IconGlyphs.Folder, UiText.Recording.OpenRawData, delegate { Shell.Open(rawDataFolder); }));
            buttons.Visibility = Visibility.Visible;
        }
        Place();
        StartLifetime();
    }

    void ShowPlaceholder(RECT work, string heading, string detail, string status)
    {
        workArea = work;
        video = null;
        ready = false;
        generation++;
        Palette.Apply(Resources);
        thumbnail.Source = null;
        thumbnail.Visibility = playChip.Visibility = lengthChip.Visibility = Visibility.Collapsed;
        placeholderGlyph.Visibility = Visibility.Visible;
        thumbnailBox.Height = CardChrome.MinThumbnailHeight;
        title.Text = heading;
        subtitle.Text = detail;
        statusGlyph.Text = status;
        buttons.Visibility = Visibility.Collapsed;
    }

    public void Present(RecordedVideo recorded, string heading, RECT work)
    {
        workArea = work;
        video = recorded;
        ready = true;
        generation++;
        Palette.Apply(Resources);
        CardChrome.ShowThumbnail(thumbnail, thumbnailBox, recorded.Thumbnail);
        thumbnail.Visibility = lengthChip.Visibility = playChip.Visibility = Visibility.Visible;
        placeholderGlyph.Visibility = recorded.Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        lengthText.Text = UiText.Recording.Length(recorded.Gif, Formats.Clock(Math.Round(recorded.Duration)));
        title.Text = heading;
        subtitle.Text = UiText.Recording.FileInfo(Path.GetFileName(recorded.Path), Formats.FileSize(recorded.Bytes));
        statusGlyph.Text = IconGlyphs.Accept;
        statusChip.Visibility = Visibility.Visible;

        buttons.Children.Clear();
        if (!recorded.Gif)
        {
            buttons.Children.Add(ActionButton(IconGlyphs.Trim, UiText.Recording.Trim, delegate { ScreenRecorder.Trim(video!, workArea); }));
            buttons.Children.Add(ActionButton(IconGlyphs.Gif, UiText.Recording.Gif, delegate { ScreenRecorder.MakeGif(video!, workArea); }));
        }
        buttons.Children.Add(ActionButton(IconGlyphs.Folder, UiText.Common.Folder, delegate { Shell.Reveal(video!.Path); }));
        buttons.Children.Add(ActionButton(IconGlyphs.Delete, UiText.Common.Delete, MoveToRecycleBin));
        buttons.Visibility = Visibility.Visible;
        Place();
        if (!card.IsMouseOver) StartLifetime(); else counting = true;
    }

    /// <summary>GIF export in progress: hide the buttons, the bar shows progress.</summary>
    public void Busy(string heading)
    {
        ready = false;
        generation++;
        title.Text = heading;
        buttons.Visibility = Visibility.Collapsed;
        counting = false;
        SetBar(0);
    }

    public void Progress(double fraction)
    {
        if (!ready) barScale.ScaleX = fraction;
    }

    public void HideNow()
    {
        generation++;
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hide();
    }

    /// <summary>Tests: stops the bar animations at a fixed width so images do not depend on timing.</summary>
    internal void SetBar(double fraction)
    {
        barMove.BeginAnimation(TranslateTransform.XProperty, null);
        barMove.X = 0;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        barScale.ScaleX = fraction;
    }

    void Place()
    {
        if (Offscreen) { card.Opacity = 1; return; }
        CardChrome.SlideIn(this, hwnd, card, slide, workArea);
    }

    void StartIndeterminateBar()
    {
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        barScale.ScaleX = 0.3;
        double trackWidth = CardChrome.ContentWidth - 6;
        barMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.3 * trackWidth, trackWidth, Motion.IndeterminateSweep)
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    // The bar runs down; at the end the card fades out (not while the mouse is over it).
    void StartLifetime()
    {
        counting = true;
        int started = generation;
        barMove.BeginAnimation(TranslateTransform.XProperty, null);
        barMove.X = 0;
        CardChrome.RunDown(barScale, LifetimeMs, delegate { if (started == generation && !card.IsMouseOver && barScale.ScaleX <= 0.001) FadeOut(); });
    }

    // Moves the file to the recycle bin so it can be restored.
    void MoveToRecycleBin()
    {
        if (!NativeMethods.MoveToRecycleBin(video!.Path)) return;
        ready = false;
        title.Text = UiText.Common.Deleted;
        subtitle.Text = UiText.Common.InRecycleBin;
        statusGlyph.Text = IconGlyphs.Delete;
        buttons.Visibility = Visibility.Collapsed;
        int deletedAt = ++generation;
        var timer = new DispatcherTimer { Interval = Motion.LingerAfterLeave };
        timer.Tick += delegate { timer.Stop(); if (deletedAt == generation) FadeOut(); };
        timer.Start();
    }

    void FadeOut()
    {
        int fadingAt = ++generation;
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardChrome.FadeOut(card, delegate
        {
            if (fadingAt != generation) return;
            Hide();
            thumbnail.Source = null;
            video = null;
        });
    }
}
