using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Card in the bottom right after a recording (like the screenshot card): thumbnail, file name and
/// size, trim / GIF / folder / delete. Also shows "saving…", GIF progress and errors.
/// </summary>
internal sealed class RecordingCard : Window
{
    const double ContentWidth = 314, MaxThumbnailHeight = 176, PlaceholderHeight = 90;
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

        thumbnail = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        playChip = BuildPlayChip();
        lengthText = new TextBlock { FontSize = 11, FontFamily = Palette.UiFont, Foreground = Palette.Light };
        lengthChip = BuildLengthChip(lengthText);
        placeholderGlyph = ToolWindows.Glyph(IconGlyphs.Video, 26);
        placeholderGlyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        thumbnailBox = BuildThumbnailBox();

        statusGlyph = ToolWindows.Glyph(IconGlyphs.Accept, 11);
        statusChip = BuildStatusChip(statusGlyph);
        title = BuildTitle();
        subtitle = BuildSubtitle();
        buttons = new UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        card = BuildCard();
        Content = card;

        HookUpMouse();
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    static Border BuildStatusChip(TextBlock glyph)
    {
        glyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Accent);
        var chip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = glyph };
        chip.SetResourceReference(Border.BackgroundProperty, Palette.AccentSoft);
        return chip;
    }

    static TextBlock BuildTitle()
    {
        var text = new TextBlock { FontFamily = Palette.TitleFont, FontSize = 17 };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        return text;
    }

    static TextBlock BuildSubtitle()
    {
        var text = new TextBlock { FontFamily = Palette.UiFont, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        return text;
    }

    Border BuildCard()
    {
        var layout = new StackPanel();
        layout.Children.Add(thumbnailBox);
        layout.Children.Add(BuildHeader());
        layout.Children.Add(buttons);
        layout.Children.Add(BuildProgressTrack());
        var border = new Border
        {
            Width = ContentWidth + 26,
            Margin = new Thickness(24),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = layout,
            Opacity = 0,
            RenderTransform = slide,
            Effect = ToolWindows.Shadow(28, 8, 0.35)
        };
        border.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        border.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return border;
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
        var box = new Border { CornerRadius = new CornerRadius(8), Width = ContentWidth, Child = layers };
        box.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        return box;
    }

    DockPanel BuildHeader()
    {
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(subtitle);
        var header = new DockPanel { Margin = new Thickness(2, 12, 2, 0) };
        header.Children.Add(statusChip);
        header.Children.Add(texts);
        return header;
    }

    // Life time countdown, indeterminate "saving" bar or GIF progress.
    Grid BuildProgressTrack()
    {
        var track = new Grid { Height = 3, Margin = new Thickness(3, 12, 3, 0), ClipToBounds = true };
        var rail = new Border { CornerRadius = new CornerRadius(1.5) };
        rail.SetResourceReference(Border.BackgroundProperty, Palette.Line);
        var transforms = new TransformGroup();
        transforms.Children.Add(barScale);
        transforms.Children.Add(barMove);
        var bar = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = transforms };
        bar.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        track.Children.Add(rail);
        track.Children.Add(bar);
        return track;
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

    Border ActionButton(string glyph, string label, Action click)
    {
        var icon = ToolWindows.Glyph(glyph, 15);
        icon.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var text = new TextBlock { Text = label, FontSize = 11, FontFamily = Palette.UiFont, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        var content = new StackPanel();
        content.Children.Add(icon);
        content.Children.Add(text);
        var button = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0, 7, 0, 6), Child = content, Cursor = Cursors.Hand };
        button.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        button.MouseEnter += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.Selected); text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text); };
        button.MouseLeave += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.Hover); text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted); };
        button.MouseLeftButtonUp += delegate { if (ready) click(); };
        return button;
    }

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
        thumbnailBox.Height = PlaceholderHeight;
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
        ShowThumbnail(recorded.Thumbnail);
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

    // Keeps the aspect ratio with rounded corners; tall images get narrower instead of cropped.
    void ShowThumbnail(BitmapSource? image)
    {
        double width = ContentWidth, height = PlaceholderHeight;
        if (image != null && image.PixelWidth > 0)
        {
            height = ContentWidth * image.PixelHeight / image.PixelWidth;
            if (height > MaxThumbnailHeight) { width = width * MaxThumbnailHeight / height; height = MaxThumbnailHeight; }
        }
        thumbnail.Source = image;
        thumbnail.Width = width;
        thumbnail.Height = height;
        thumbnail.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        thumbnail.Visibility = lengthChip.Visibility = playChip.Visibility = Visibility.Visible;
        placeholderGlyph.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        thumbnailBox.Height = Math.Max(height, PlaceholderHeight);
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

    // Bottom right of the work area; slides in softly when first shown.
    void Place()
    {
        if (Offscreen) { card.Opacity = 1; return; }
        bool appearing = !IsVisible;
        if (appearing)
        {
            Left = -10000;
            Show();
        }
        UpdateLayout();
        double scale = ToolWindows.Scale(hwnd);
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        NativeMethods.MoveTopmost(hwnd, workArea.right - width, workArea.bottom - height);
        var duration = Motion.SlideIn;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, duration) { EasingFunction = ease };
        var rise = new DoubleAnimation(0, duration) { EasingFunction = ease };
        if (appearing) { fade.From = 0; rise.From = 12; }
        card.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    void StartIndeterminateBar()
    {
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        barScale.ScaleX = 0.3;
        double trackWidth = ContentWidth - 6;
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
        var run = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(LifetimeMs)));
        run.Completed += delegate { if (started == generation && !card.IsMouseOver && barScale.ScaleX <= 0.001) FadeOut(); };
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, run);
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
        var fade = new DoubleAnimation(0, Motion.FadeOut) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += delegate
        {
            if (fadingAt != generation) return;
            Hide();
            thumbnail.Source = null;
            video = null;
        };
        card.BeginAnimation(OpacityProperty, fade);
    }
}
