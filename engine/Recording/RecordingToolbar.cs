using System.Windows.Documents;

namespace MosaikCapture.Engine.Recording;

/// <summary>
/// Toolbar shown while recording: time, microphone, system sound, click highlights, pause, stop and
/// discard. It can be dragged by its grip and is invisible in the video.
/// </summary>
internal sealed class RecordingToolbar : Window
{
    const double ShadowRoom = 20, CaptionRowHeight = 36;
    readonly Border pill, grip, caption, microphoneButton, pauseButton, stopButton, clickButton;
    readonly TextBlock captionText, microphoneGlyph, soundGlyph, clickGlyph, pauseGlyph, discardGlyph;
    readonly Shapes.Ellipse dot;
    readonly Canvas captionRow;
    readonly ScaleTransform levelScale = new(0, 1);
    readonly DispatcherTimer refreshTimer, confirmTimer;
    readonly Dictionary<Border, Func<string>> captions = new();
    bool dragging;
    int dragStartX, dragStartY, windowStartX, windowStartY;
    Border? hovered;

    public RecordingToolbar()
    {
        ToolWindows.Setup(this, UiText.Titles.Toolbar);
        SizeToContent = SizeToContent.WidthAndHeight;

        dot = new Shapes.Ellipse { Width = 10, Height = 10, Fill = Palette.RecordingRed, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        TimeText = ToolWindows.Text(14, Palette.Text);
        grip = BuildGrip();

        microphoneGlyph = ToolWindows.Glyph("", 16);
        microphoneButton = AddButton(BuildMicrophoneIcon(), () => ScreenRecorder.Mic ? UiText.Toolbar.MicrophoneOn : UiText.Toolbar.MicrophoneOff,
            delegate { ScreenRecorder.SetMic(!ScreenRecorder.Mic); Refresh(); });
        soundGlyph = ToolWindows.Glyph("", 16);
        SoundButton = AddButton(soundGlyph, () => ScreenRecorder.Sound ? UiText.Toolbar.SoundOn : UiText.Toolbar.SoundOff,
            delegate { ScreenRecorder.SetSound(!ScreenRecorder.Sound); Refresh(); });
        clickGlyph = ToolWindows.Glyph(IconGlyphs.Touch, 16);
        clickButton = AddButton(clickGlyph, () => ScreenRecorder.Clicks ? UiText.Toolbar.ClicksOn : UiText.Toolbar.ClicksOff,
            delegate { ScreenRecorder.SetClicks(!ScreenRecorder.Clicks); Refresh(); });
        pauseGlyph = ToolWindows.Glyph("", 16);
        pauseButton = AddButton(pauseGlyph, () => ScreenRecorder.Paused ? UiText.Toolbar.Resume : UiText.Toolbar.Pause, ScreenRecorder.TogglePause);
        var square = new Shapes.Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 2.5,
            RadiusY = 2.5,
            Fill = Palette.Light,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stopButton = AddButton(square, () => UiText.Toolbar.Stop, ScreenRecorder.Stop);
        stopButton.Background = Palette.RecordingRed;
        discardGlyph = ToolWindows.Glyph(IconGlyphs.Delete, 16);
        DiscardButton = AddButton(discardGlyph, () => ConfirmingDiscard ? UiText.Toolbar.ConfirmDiscard : UiText.Toolbar.Discard, Discard);

        pill = BuildPill();
        captionText = ToolWindows.Text(12, Palette.Text);
        caption = ToolWindows.Chip(captionText);
        caption.Visibility = Visibility.Hidden;
        captionRow = new Canvas { Height = CaptionRowHeight };
        captionRow.Children.Add(caption);
        Content = BuildLayout();

        Handle = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        ToolWindows.ExcludeFromCapture(Handle);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        refreshTimer.Tick += delegate { UpdateTimeAndLevel(); };
        confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        confirmTimer.Tick += delegate { confirmTimer.Stop(); ConfirmingDiscard = false; Refresh(); };
    }

    public IntPtr Handle { get; }

    // Exposed for the pixel tests.
    internal TextBlock TimeText { get; }
    internal Border SoundButton { get; }
    internal Border DiscardButton { get; }
    /// <summary>The discard button was clicked once and waits for the confirming second click.</summary>
    internal bool ConfirmingDiscard { get; set; }

    // ---------- Building ----------

    // Red dot and time; drag here to move the toolbar.
    Border BuildGrip()
    {
        TimeText.FontWeight = FontWeights.SemiBold;
        TimeText.MinWidth = 40;
        Typography.SetNumeralAlignment(TimeText, FontNumeralAlignment.Tabular);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(dot);
        content.Children.Add(TimeText);
        var handle = new Border { Padding = new Thickness(12, 0, 10, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = content };
        captions[handle] = () => UiText.Toolbar.Move;
        handle.MouseEnter += delegate { Hover(handle); };
        handle.MouseLeave += delegate { Hover(null); };
        handle.MouseLeftButtonDown += (_, _) => StartDrag();
        handle.MouseMove += (_, _) => DragTo();
        handle.MouseLeftButtonUp += (_, _) => { dragging = false; handle.ReleaseMouseCapture(); };
        return handle;
    }

    // Microphone with a small level meter below it.
    Grid BuildMicrophoneIcon()
    {
        var level = new Border
        {
            Width = 14,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5),
            RenderTransform = levelScale,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        level.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        var icon = new Grid();
        icon.Children.Add(microphoneGlyph);
        icon.Children.Add(level);
        return icon;
    }

    Border BuildPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var element in new UIElement[] { grip, Separator(), microphoneButton, SoundButton, clickButton, Separator(), pauseButton, stopButton, Separator(), DiscardButton })
            row.Children.Add(element);
        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(5),
            BorderThickness = new Thickness(1),
            Child = row,
            Effect = ToolWindows.Shadow(24, 6, 0.35)
        };
        border.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        border.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return border;
    }

    // Button captions appear above the toolbar (inside the window, so also not in the video).
    Grid BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(ShadowRoom, 0, ShadowRoom, ShadowRoom) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionRowHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(captionRow);
        Grid.SetRow(pill, 1);
        root.Children.Add(pill);
        return root;
    }

    static Border Separator()
    {
        var line = new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Border.BackgroundProperty, Palette.Line);
        return line;
    }

    Border AddButton(UIElement content, Func<string> captionFor, Action click)
    {
        var button = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(1, 0, 1, 0), Child = content, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        captions[button] = captionFor;
        button.MouseEnter += delegate { Hover(button); };
        button.MouseLeave += delegate { Hover(null); };
        button.MouseLeftButtonUp += delegate { click(); if (hovered == button) Hover(button); };
        return button;
    }

    // ---------- State ----------

    /// <summary>Highlights a button and shows its caption above it.</summary>
    internal void Hover(Border? button)
    {
        hovered = button;
        Refresh();
        if (button == null) { caption.Visibility = Visibility.Hidden; return; }
        captionText.Text = captions[button]();
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point anchor = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), captionRow);
        double width = caption.DesiredSize.Width, maxLeft = captionRow.ActualWidth - width;
        Canvas.SetLeft(caption, Math.Round(Math.Max(0, Math.Min(maxLeft, anchor.X - width / 2))));
        Canvas.SetTop(caption, CaptionRowHeight - caption.DesiredSize.Height - 6);
        caption.Visibility = Visibility.Visible;
    }

    public void Present(Int32Rect area, RECT work)
    {
        Palette.Apply(Resources);
        ConfirmingDiscard = false;
        hovered = null;
        caption.Visibility = Visibility.Hidden;
        Refresh();
        UpdateTimeAndLevel();
        Left = -10000;
        Show();
        UpdateLayout();
        PlaceNear(area, work);
        refreshTimer.Start();
    }

    // Below the area, else above it, else inside along its bottom edge (full screen).
    void PlaceNear(Int32Rect area, RECT work)
    {
        double scale = ToolWindows.Scale(Handle);
        int pillWidth = (int)Math.Round(pill.ActualWidth * scale), pillHeight = (int)Math.Round(pill.ActualHeight * scale);
        int gap = (int)Math.Round(14 * scale), margin = (int)Math.Round(10 * scale);
        int x = area.X + (area.Width - pillWidth) / 2, y = area.Y + area.Height + gap;
        if (y + pillHeight > work.bottom - margin)
        {
            y = area.Y - gap - pillHeight;
            if (y < work.top + margin) y = Math.Min(area.Y + area.Height, work.bottom) - (int)Math.Round(24 * scale) - pillHeight;
        }
        x = Math.Max(work.left + margin, Math.Min(work.right - margin - pillWidth, x));
        NativeMethods.MoveTopmost(Handle, x - (int)Math.Round(ShadowRoom * scale), y - (int)Math.Round(CaptionRowHeight * scale));
    }

    public new void Hide()
    {
        refreshTimer.Stop();
        confirmTimer.Stop();
        dot.BeginAnimation(OpacityProperty, null);
        base.Hide();
    }

    /// <summary>Updates icons and highlights from the recorder's state.</summary>
    public void Refresh()
    {
        bool paused = ScreenRecorder.Paused;
        microphoneGlyph.Text = ScreenRecorder.Mic ? IconGlyphs.Microphone : IconGlyphs.MicrophoneOff;
        soundGlyph.Text = ScreenRecorder.Sound ? IconGlyphs.Volume : IconGlyphs.Mute;
        pauseGlyph.Text = paused ? IconGlyphs.Play : IconGlyphs.Pause;
        PaintGlyph(microphoneGlyph, ScreenRecorder.Mic ? Palette.Text : null);
        PaintGlyph(soundGlyph, ScreenRecorder.Sound ? Palette.Text : null);
        PaintGlyph(clickGlyph, ScreenRecorder.Clicks ? Palette.Text : Palette.Muted);
        PaintGlyph(pauseGlyph, Palette.Text);
        foreach (var button in new[] { microphoneButton, SoundButton, clickButton, pauseButton })
            PaintHover(button);
        stopButton.Opacity = hovered == stopButton ? 0.86 : 1;
        if (ConfirmingDiscard)
        {
            DiscardButton.Background = Palette.RecordingRed;
            discardGlyph.Foreground = Palette.Light;
        }
        else
        {
            PaintHover(DiscardButton);
            PaintGlyph(discardGlyph, Palette.Text);
        }
        RefreshDot(paused);
        if (hovered != null && caption.Visibility == Visibility.Visible) captionText.Text = captions[hovered]();
    }

    void PaintHover(Border button)
    {
        if (button == hovered) button.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        else button.Background = Brushes.Transparent;
    }

    // The dot pulses while recording; when paused it stays still and muted.
    void RefreshDot(bool paused)
    {
        if (paused)
        {
            dot.BeginAnimation(OpacityProperty, null);
            dot.Opacity = 1;
            dot.SetResourceReference(Shapes.Shape.FillProperty, Palette.Muted);
            TimeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
            return;
        }
        dot.Fill = Palette.RecordingRed;
        TimeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        if (IsVisible && !dot.HasAnimatedProperties)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(800)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    // null = "off" in recording red
    static void PaintGlyph(TextBlock glyph, string? brushKey)
    {
        if (brushKey == null) glyph.Foreground = Palette.RecordingRed;
        else glyph.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    void UpdateTimeAndLevel()
    {
        TimeText.Text = Formats.Clock(ScreenRecorder.Elapsed);
        double level = ScreenRecorder.Mic && !ScreenRecorder.Paused ? Math.Min(1, Math.Sqrt(ScreenRecorder.MicLevel) * 1.2) : 0;
        levelScale.ScaleX = level < 0.04 ? 0 : level;
        if (!ScreenRecorder.Paused && !dot.HasAnimatedProperties) Refresh();
    }

    // First click: turn red; second click within 3 s: discard.
    void Discard()
    {
        if (ConfirmingDiscard)
        {
            ConfirmingDiscard = false;
            confirmTimer.Stop();
            ScreenRecorder.Discard();
            return;
        }
        ConfirmingDiscard = true;
        confirmTimer.Stop();
        confirmTimer.Start();
        Refresh();
    }

    void StartDrag()
    {
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.GetWindowRect(Handle, out RECT window);
        dragStartX = cursor.x;
        dragStartY = cursor.y;
        windowStartX = window.left;
        windowStartY = window.top;
        dragging = true;
        grip.CaptureMouse();
    }

    void DragTo()
    {
        if (!dragging) return;
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.MoveTopmost(Handle, windowStartX + cursor.x - dragStartX, windowStartY + cursor.y - dragStartY);
    }
}
