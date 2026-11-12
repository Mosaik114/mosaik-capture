using System.Windows.Controls.Primitives;

namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Card in the bottom right after a capture: preview, file name, edit / pin / text / folder / delete.
/// Click the preview = open the image, drag the preview = drop the file into another app.
/// Never takes focus; disappears after 5 seconds (not while the mouse is over it).
/// </summary>
internal sealed class ScreenshotCard : Window
{
    const double ContentWidth = 314, MaxThumbnailHeight = 176, MinThumbnailBoxHeight = 90;
    const int LifetimeMs = 5000;
    readonly IntPtr hwnd;
    readonly Border card, thumbnailBox;
    readonly Image thumbnail;
    readonly TextBlock title, subtitle, sizeText, statusGlyph;
    readonly UniformGrid buttons;
    readonly ScaleTransform lifetimeBar = new(1, 1);
    readonly TranslateTransform slide = new();
    readonly Action edit, pin, closed;
    string path = "";
    bool saved, pressing;
    int generation;   // invalidates pending timers and animations of an earlier state
    Point pressedAt;
    BitmapSource? shownImage;

    public ScreenshotCard(Action onEdit, Action onPin, Action onClosed)
    {
        edit = onEdit;
        pin = onPin;
        closed = onClosed;
        ToolWindows.Setup(this, UiText.Titles.ScreenshotCard);
        SizeToContent = SizeToContent.WidthAndHeight;

        thumbnail = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sizeText = new TextBlock { FontSize = 11, FontFamily = Palette.UiFont };
        thumbnailBox = BuildThumbnailBox();
        statusGlyph = new TextBlock { Text = IconGlyphs.Accept, FontSize = 11, FontFamily = Palette.IconFont, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        title = new TextBlock { FontFamily = Palette.TitleFont, FontSize = 17 };
        subtitle = new TextBlock { FontFamily = Palette.UiFont, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        buttons = BuildButtons();

        var layout = new StackPanel();
        layout.Children.Add(thumbnailBox);
        layout.Children.Add(BuildHeader());
        layout.Children.Add(buttons);
        layout.Children.Add(BuildLifetimeTrack());
        card = new Border
        {
            Width = ContentWidth + 26,
            Margin = new Thickness(24),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = layout,
            Opacity = 0,
            RenderTransform = slide,
            Effect = Metrics.CardShadow()
        };
        card.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        card.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        Content = card;

        HookUpMouse();
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    // ---------- Building ----------

    Border BuildThumbnailBox()
    {
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var sizeChip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = sizeText,
            Background = new SolidColorBrush(Palette.ImageChip.Color),
            IsHitTestVisible = false
        };
        var layers = new Grid();
        layers.Children.Add(thumbnail);
        layers.Children.Add(sizeChip);
        var box = new Border { CornerRadius = new CornerRadius(8), Width = ContentWidth, Child = layers };
        box.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        return box;
    }

    DockPanel BuildHeader()
    {
        statusGlyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Accent);
        var statusChip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = statusGlyph };
        statusChip.SetResourceReference(Border.BackgroundProperty, Palette.AccentSoft);
        title.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(subtitle);
        var header = new DockPanel { Margin = new Thickness(2, 12, 2, 0) };
        header.Children.Add(statusChip);
        header.Children.Add(texts);
        return header;
    }

    UniformGrid BuildButtons()
    {
        var row = new UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(ActionButton(IconGlyphs.Edit, UiText.ScreenshotCard.Edit, delegate { if (saved) edit(); }));
        row.Children.Add(ActionButton(IconGlyphs.Pin, UiText.ScreenshotCard.Pin, delegate { pin(); FadeOut(); }));
        row.Children.Add(ActionButton(IconGlyphs.TextRecognition, UiText.ScreenshotCard.Text, ReadText));
        row.Children.Add(ActionButton(IconGlyphs.Folder, UiText.Common.Folder, delegate { if (saved) Shell.Reveal(path); }));
        row.Children.Add(ActionButton(IconGlyphs.Delete, UiText.Common.Delete, MoveToRecycleBin));
        return row;
    }

    Grid BuildLifetimeTrack()
    {
        var track = new Grid { Height = 3, Margin = new Thickness(3, 12, 3, 0) };
        var rail = new Border { CornerRadius = new CornerRadius(1.5) };
        rail.SetResourceReference(Border.BackgroundProperty, Palette.Line);
        var bar = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = lifetimeBar };
        bar.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        track.Children.Add(rail);
        track.Children.Add(bar);
        return track;
    }

    void HookUpMouse()
    {
        // The card stays while the mouse is over it
        card.MouseEnter += delegate { lifetimeBar.BeginAnimation(ScaleTransform.ScaleXProperty, null); lifetimeBar.ScaleX = 1; };
        card.MouseLeave += delegate { if (IsVisible && !pressing) StartLifetime(); };
        card.MouseRightButtonUp += delegate { FadeOut(); };

        thumbnail.MouseLeftButtonDown += (_, e) => { pressedAt = e.GetPosition(this); pressing = true; thumbnail.CaptureMouse(); e.Handled = true; };
        thumbnail.MouseMove += (_, e) =>
        {
            if (!pressing || !saved) return;
            Vector moved = e.GetPosition(this) - pressedAt;
            if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6) return;
            pressing = false;
            thumbnail.ReleaseMouseCapture();
            DragDrop.DoDragDrop(thumbnail, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);   // drag into chat, mail, Explorer ...
            if (!card.IsMouseOver) StartLifetime();
        };
        thumbnail.MouseLeftButtonUp += (_, _) =>
        {
            if (!pressing) return;
            pressing = false;
            thumbnail.ReleaseMouseCapture();
            if (saved) { Shell.Open(path); FadeOut(); }
        };
    }

    static Border ActionButton(string glyph, string label, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 15, FontFamily = Palette.IconFont, HorizontalAlignment = HorizontalAlignment.Center };
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
        button.MouseLeftButtonUp += delegate { click(); };
        return button;
    }

    // ---------- States ----------

    public void Present(ScreenCapture capture, string heading)
    {
        BitmapSource image = capture.Image;
        Palette.Apply(Resources);
        path = capture.Path;
        saved = false;
        generation++;
        thumbnailBox.Visibility = Visibility.Visible;
        ShowThumbnail(image);
        sizeText.Text = UiText.Common.Size(image.PixelWidth, image.PixelHeight);
        title.Text = heading;
        subtitle.Text = Path.GetFileName(capture.Path);
        statusGlyph.Text = IconGlyphs.Accept;
        buttons.Visibility = Visibility.Visible;
        SlideIn(capture.WorkArea);
        if (!card.IsMouseOver) StartLifetime();
    }

    // Keeps the aspect ratio with rounded corners; tall images get narrower instead of cropped.
    void ShowThumbnail(BitmapSource image)
    {
        double width = ContentWidth, height = ContentWidth * image.PixelHeight / image.PixelWidth;
        if (height > MaxThumbnailHeight) { width = width * MaxThumbnailHeight / height; height = MaxThumbnailHeight; }
        thumbnail.Source = image;
        shownImage = image;
        thumbnail.Width = width;
        thumbnail.Height = height;
        thumbnail.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        thumbnailBox.Height = Math.Max(height, MinThumbnailBoxHeight);
    }

    // Bottom right of the work area; slides in softly when first shown.
    void SlideIn(RECT workArea)
    {
        bool appearing = !IsVisible;
        if (appearing)
        {
            Left = -10000;   // measure first, then move to the edge
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

    public void Saved() => saved = true;

    /// <summary>Saved, but the clipboard was busy: the heading must not claim it was copied.</summary>
    public void SavedNotCopied()
    {
        saved = true;
        title.Text = UiText.ScreenshotCard.SavedNotCopied;
    }

    /// <summary>An unexpected error in the screenshot flow, shown without an image.</summary>
    public void ShowProblem(string heading, string detail, RECT workArea)
    {
        Palette.Apply(Resources);
        path = "";
        saved = false;
        generation++;
        thumbnail.Source = null;
        shownImage = null;
        thumbnailBox.Visibility = Visibility.Collapsed;
        title.Text = heading;
        subtitle.Text = detail;
        statusGlyph.Text = IconGlyphs.Cancel;
        buttons.Visibility = Visibility.Collapsed;
        SlideIn(workArea);
        if (!card.IsMouseOver) StartLifetime();
    }

    // Recognizes and copies the text in the image; the result is shown on the card.
    void ReadText() => ReadTextAsync().OnFailure("Recognizing text on the card", () => title.Text = UiText.Common.TextRecognitionUnavailable);

    async Task ReadTextAsync()
    {
        var image = shownImage;
        if (image == null) return;
        int startedAt = generation;
        title.Text = UiText.Common.ReadingText;
        var result = await TextRecognition.ReadAsync(image);
        if (startedAt != generation) return;
        if (result.Status != TextRecognitionStatus.Recognized)
        {
            title.Text = result.FailureMessage;
            return;
        }
        bool copied = await ClipboardWriter.SetTextAsync(result.Text);
        if (startedAt != generation) return;
        title.Text = copied ? UiText.Common.TextCopied : UiText.Common.CopyFailed;
        subtitle.Text = TextRecognition.Summary(result.Text);
    }

    /// <summary>Hides immediately for the editor without releasing the capture.</summary>
    public void HideNow()
    {
        generation++;
        lifetimeBar.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hide();
    }

    public void Failed()
    {
        title.Text = UiText.ScreenshotCard.NotSaved;
        subtitle.Text = UiText.ScreenshotCard.FolderNotWritable(Screenshots.Folder);
        buttons.Visibility = Visibility.Collapsed;
    }

    // The bar runs down; at the end the card fades out (not while the mouse is over it).
    void StartLifetime()
    {
        int started = generation;
        var run = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(LifetimeMs)));
        run.Completed += delegate { if (started == generation && !card.IsMouseOver && lifetimeBar.ScaleX <= 0.001) FadeOut(); };
        lifetimeBar.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }

    // Moves the file to the recycle bin so it can be restored.
    void MoveToRecycleBin()
    {
        if (!saved || !NativeMethods.MoveToRecycleBin(path)) return;
        saved = false;
        title.Text = UiText.Common.Deleted;
        subtitle.Text = UiText.Common.InRecycleBin;
        statusGlyph.Text = IconGlyphs.Delete;
        buttons.Visibility = Visibility.Collapsed;
        generation++;
        var timer = new DispatcherTimer { Interval = Motion.LingerAfterLeave };
        timer.Tick += delegate { timer.Stop(); FadeOut(); };
        timer.Start();
    }

    void FadeOut()
    {
        int fadingAt = ++generation;
        lifetimeBar.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        var fade = new DoubleAnimation(0, Motion.FadeOut) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += delegate
        {
            if (fadingAt != generation) return;
            Hide();
            thumbnail.Source = null;
            closed();
        };
        card.BeginAnimation(OpacityProperty, fade);
    }
}
