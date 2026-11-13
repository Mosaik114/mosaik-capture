using System.Windows.Documents;

namespace MosaikCapture.Engine.Recording;

// Trim window: building the controls (fields and construction in TrimWindow.cs).
internal sealed partial class TrimWindow
{
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

    // Button with icon and label; the primary button uses the accent fill (darker while hovered).
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
        string background = primary ? Palette.AccentFill : Palette.Hover;
        button.SetResourceReference(Border.BackgroundProperty, background);
        if (primary)
        {
            button.MouseEnter += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.AccentFillHover); };
            button.MouseLeave += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.AccentFill); };
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
        edge.SetResourceReference(Border.BackgroundProperty, Palette.AccentFill);   // same color as the handles
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
        handle.SetResourceReference(Border.BackgroundProperty, Palette.AccentFill);   // carries the light grip
        return handle;
    }
}
