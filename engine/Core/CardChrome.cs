using System.Windows.Controls.Primitives;

namespace MosaikCapture.Engine.Core;

/// <summary>
/// What the screenshot card and the recording card share: the frame, the thumbnail box, the header (status
/// chip, title, subtitle), the action buttons, the bar at the bottom and how the card slides in and fades
/// out at the bottom right of the work area. Each card keeps its own content and states.
/// </summary>
internal static class CardChrome
{
    /// <summary>Width of the thumbnail and everything below it.</summary>
    public const double ContentWidth = 314;
    public const double MaxThumbnailHeight = 176, MinThumbnailHeight = 90;

    /// <summary>The card itself: rounded, themed, with a shadow; invisible until it slides in.</summary>
    public static Border Frame(UIElement content, TranslateTransform slide)
    {
        var frame = new Border
        {
            Width = ContentWidth + 26,
            Margin = new Thickness(24),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = content,
            Opacity = 0,
            RenderTransform = slide,
            Effect = Metrics.CardShadow()
        };
        frame.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        frame.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return frame;
    }

    /// <summary>The preview image: click to open, drag to drop the file elsewhere.</summary>
    public static Image Thumbnail() => new() { Stretch = Stretch.Fill, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Rounded box behind the thumbnail and its chips.</summary>
    public static Border ThumbnailBox(UIElement layers)
    {
        var box = new Border { CornerRadius = new CornerRadius(8), Width = ContentWidth, Child = layers };
        box.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        return box;
    }

    /// <summary>
    /// Shows <paramref name="image"/> in its aspect ratio with rounded corners; tall images get narrower instead
    /// of cropped. Without an image the box keeps its minimum height.
    /// </summary>
    public static void ShowThumbnail(Image thumbnail, Border box, BitmapSource? image)
    {
        double width = ContentWidth, height = MinThumbnailHeight;
        if (image != null && image.PixelWidth > 0)
        {
            height = ContentWidth * image.PixelHeight / image.PixelWidth;
            if (height > MaxThumbnailHeight) { width = width * MaxThumbnailHeight / height; height = MaxThumbnailHeight; }
        }
        thumbnail.Source = image;
        thumbnail.Width = width;
        thumbnail.Height = height;
        thumbnail.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        box.Height = Math.Max(height, MinThumbnailHeight);
    }

    /// <summary>The round chip left of the title (done, warning, deleted ...).</summary>
    public static Border StatusChip(TextBlock glyph)
    {
        glyph.SetResourceReference(TextBlock.ForegroundProperty, Palette.Accent);
        var chip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = glyph };
        chip.SetResourceReference(Border.BackgroundProperty, Palette.AccentSoft);
        return chip;
    }

    public static TextBlock Title()
    {
        var text = new TextBlock { FontFamily = Palette.TitleFont, FontSize = 17 };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        return text;
    }

    public static TextBlock Subtitle()
    {
        var text = new TextBlock { FontFamily = Palette.UiFont, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        return text;
    }

    /// <summary>Status chip, then title above subtitle.</summary>
    public static DockPanel Header(Border statusChip, TextBlock title, TextBlock subtitle)
    {
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(subtitle);
        var header = new DockPanel { Margin = new Thickness(2, 12, 2, 0) };
        header.Children.Add(statusChip);
        header.Children.Add(texts);
        return header;
    }

    /// <summary>The row the <see cref="ActionButton"/>s go into, all equally wide.</summary>
    public static UniformGrid ButtonRow() => new() { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };

    /// <summary>Icon above a label; <paramref name="click"/> runs on release.</summary>
    public static Border ActionButton(string glyph, string label, Action click)
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
        button.MouseLeftButtonUp += delegate { click(); };
        return button;
    }

    /// <summary>The thin bar at the bottom (lifetime or progress); <paramref name="bar"/> scales and moves it.</summary>
    public static Grid BarTrack(Transform bar)
    {
        var track = new Grid { Height = 3, Margin = new Thickness(3, 12, 3, 0) };
        var rail = new Border { CornerRadius = new CornerRadius(1.5) };
        rail.SetResourceReference(Border.BackgroundProperty, Palette.Line);
        var fill = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = bar };
        fill.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        track.Children.Add(rail);
        track.Children.Add(fill);
        return track;
    }

    /// <summary>Lets the bar run down from full to empty; <paramref name="ended"/> runs when it got there.</summary>
    public static void RunDown(ScaleTransform bar, int milliseconds, Action ended)
    {
        var run = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(milliseconds)));
        run.Completed += delegate { ended(); };
        bar.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }

    /// <summary>Places the window at the bottom right of the work area; slides in softly when it was hidden.</summary>
    public static void SlideIn(Window window, IntPtr hwnd, UIElement card, TranslateTransform slide, RECT workArea)
    {
        bool appearing = !window.IsVisible;
        if (appearing)
        {
            window.Left = -10000;   // measure first, then move to the edge
            window.Show();
        }
        window.UpdateLayout();
        double scale = ToolWindows.Scale(hwnd);
        int width = (int)Math.Round(window.ActualWidth * scale), height = (int)Math.Round(window.ActualHeight * scale);
        NativeMethods.MoveTopmost(hwnd, workArea.right - width, workArea.bottom - height);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, Motion.SlideIn) { EasingFunction = ease };
        var rise = new DoubleAnimation(0, Motion.SlideIn) { EasingFunction = ease };
        if (appearing) { fade.From = 0; rise.From = 12; }
        card.BeginAnimation(UIElement.OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Fades the card out; <paramref name="faded"/> runs at the end (the card decides whether that still counts).</summary>
    public static void FadeOut(UIElement card, Action faded)
    {
        var fade = new DoubleAnimation(0, Motion.FadeOut) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += delegate { faded(); };
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
