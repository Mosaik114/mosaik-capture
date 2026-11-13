using System.Windows.Controls.Primitives;

namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Card in the bottom right after a capture (the frame is shared with the recording card, see
/// <see cref="CardChrome"/>): preview, file name, edit / pin / text / folder / delete.
/// Click the preview = open the image, drag the preview = drop the file into another app.
/// Never takes focus; disappears after 5 seconds (not while the mouse is over it).
/// </summary>
internal sealed class ScreenshotCard : Window
{
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

        thumbnail = CardChrome.Thumbnail();
        sizeText = new TextBlock { FontSize = 11, FontFamily = Palette.UiFont };
        thumbnailBox = BuildThumbnailBox();
        statusGlyph = ToolWindows.Glyph(IconGlyphs.Accept, 11);
        title = CardChrome.Title();
        subtitle = CardChrome.Subtitle();
        buttons = BuildButtons();

        var layout = new StackPanel();
        layout.Children.Add(thumbnailBox);
        layout.Children.Add(CardChrome.Header(CardChrome.StatusChip(statusGlyph), title, subtitle));
        layout.Children.Add(buttons);
        layout.Children.Add(CardChrome.BarTrack(lifetimeBar));
        card = CardChrome.Frame(layout, slide);
        Content = card;

        HookUpMouse();
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>Tests: build the content but never show the window.</summary>
    internal bool Offscreen { get; set; }

    /// <summary>Tests: stops the lifetime bar at a fixed width so images do not depend on timing.</summary>
    internal void SetBar(double fraction)
    {
        lifetimeBar.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        lifetimeBar.ScaleX = fraction;
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
        return CardChrome.ThumbnailBox(layers);
    }

    UniformGrid BuildButtons()
    {
        var row = CardChrome.ButtonRow();
        row.Children.Add(CardChrome.ActionButton(IconGlyphs.Edit, UiText.ScreenshotCard.Edit, delegate { if (saved) edit(); }));
        row.Children.Add(CardChrome.ActionButton(IconGlyphs.Pin, UiText.ScreenshotCard.Pin, delegate { pin(); FadeOut(); }));
        row.Children.Add(CardChrome.ActionButton(IconGlyphs.TextRecognition, UiText.ScreenshotCard.Text, ReadText));
        row.Children.Add(CardChrome.ActionButton(IconGlyphs.Folder, UiText.Common.Folder, delegate { if (saved) Shell.Reveal(path); }));
        row.Children.Add(CardChrome.ActionButton(IconGlyphs.Delete, UiText.Common.Delete, MoveToRecycleBin));
        return row;
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

    // ---------- States ----------

    public void Present(ScreenCapture capture, string heading)
    {
        BitmapSource image = capture.Image;
        Palette.Apply(Resources);
        path = capture.Path;
        saved = false;
        generation++;
        thumbnailBox.Visibility = Visibility.Visible;
        CardChrome.ShowThumbnail(thumbnail, thumbnailBox, image);
        shownImage = image;
        sizeText.Text = UiText.Common.Size(image.PixelWidth, image.PixelHeight);
        title.Text = heading;
        subtitle.Text = Path.GetFileName(capture.Path);
        statusGlyph.Text = IconGlyphs.Accept;
        buttons.Visibility = Visibility.Visible;
        SlideIn(capture.WorkArea);
        if (!card.IsMouseOver) StartLifetime();
    }

    void SlideIn(RECT workArea)
    {
        if (Offscreen) { card.Opacity = 1; return; }
        CardChrome.SlideIn(this, hwnd, card, slide, workArea);
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
        CardChrome.RunDown(lifetimeBar, LifetimeMs, delegate { if (started == generation && !card.IsMouseOver && lifetimeBar.ScaleX <= 0.001) FadeOut(); });
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
        CardChrome.FadeOut(card, delegate
        {
            if (fadingAt != generation) return;
            Hide();
            thumbnail.Source = null;
            closed();
        });
    }
}
