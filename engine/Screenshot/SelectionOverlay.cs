namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Full-screen window across all monitors showing the frozen screen, for picking a screenshot or
/// recording area: drag = area, click = window below, Space = whole monitor, C = copy color.
/// It is not a transparent window (too sluggish at 5120 x 1440 pixels); instead it is only shown
/// once it has been rendered (DWM cloak), then only the dimming fades in.
/// </summary>
internal sealed class SelectionOverlay : Window
{
    const double LoupeSize = 130;
    const int LoupePixels = 13;              // pixels per row in the loupe
    const double LoupeCell = LoupeSize / LoupePixels;
    const double DragThreshold = 4;          // movement in device pixels that turns a click into a drag
    const double MinimumAreaSize = 3;

    readonly Action<ScreenCapture>? captured;
    readonly IntPtr hwnd;
    readonly double scale;                   // device pixels per WPF unit
    readonly Canvas root, decorations, loupe;
    readonly Image frozenScreen, loupeImage;
    readonly Shapes.Path dimming;
    readonly Shapes.Rectangle frame, loupeCenter, swatch;
    readonly Shapes.Line horizontalGuide, verticalGuide;
    readonly Border sizeChip, colorChip, hint;
    readonly TextBlock sizeText, colorText, positionText, hintText;
    readonly List<Rect> windows = new();     // visible windows from top to bottom, relative to the virtual screen
    readonly List<RECT> monitors = new(), workAreas = new();
    readonly EngineWork shown = new();       // counts as busy while open: the engine must not quit under the user

    BitmapSource? screen;
    Int32Rect virtualScreen;                 // in device pixels
    Point pressedAt;
    bool pressed, dragging;
    Rect selection = Rect.Empty;
    string color = "";
    bool forRecording;                       // picking an area for a screen recording instead of a screenshot

    static readonly SolidColorBrush GuideBrush = Palette.Frozen(0x66, 0xD9, 0x77, 0x57);
    static readonly DoubleCollection GuideDashes = Freeze(new DoubleCollection(new double[] { 4, 4 }));

    static DoubleCollection Freeze(DoubleCollection dashes)
    {
        dashes.Freeze();
        return dashes;
    }

    public SelectionOverlay(Action<ScreenCapture>? onCaptured)
    {
        captured = onCaptured;
        ConfigureWindow();

        root = new Canvas();
        frozenScreen = new Image { Stretch = Stretch.Fill };
        decorations = new Canvas { IsHitTestVisible = false };
        dimming = new Shapes.Path { Fill = Palette.Dimming };
        horizontalGuide = BuildGuide();
        verticalGuide = BuildGuide();
        frame = new Shapes.Rectangle { StrokeThickness = 2 };
        frame.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
        sizeText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Palette.UiFont };
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        sizeChip = ToolWindows.Chip(sizeText);
        loupeImage = new Image { Width = LoupeSize, Height = LoupeSize, Stretch = Stretch.Fill };
        loupeCenter = new Shapes.Rectangle { Width = LoupeCell + 2, Height = LoupeCell + 2, StrokeThickness = 2 };
        loupe = BuildLoupe();
        swatch = new Shapes.Rectangle { Width = 11, Height = 11, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        colorText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Palette.UiFont };
        positionText = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), FontFamily = Palette.UiFont };
        colorChip = BuildColorChip();
        hintText = new TextBlock { FontSize = 12.5, FontFamily = Palette.UiFont, Text = UiText.Overlay.ScreenshotHint };
        hint = BuildHint();
        ComposeLayers();

        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_TOOLWINDOW);   // not in Alt+Tab
        scale = ToolWindows.Scale(hwnd);
        HookUpInput();
    }

    /// <summary>Recording: the selected area in device pixels (absolute), plus monitor and work area.</summary>
    public Action<Int32Rect, RECT, RECT>? Recorded { get; set; }
    public Action? Aborted { get; set; }

    // ---------- Building ----------

    // Magnified pixels with a grid; the pixel under the pointer is outlined.
    Canvas BuildLoupe()
    {
        var container = new Canvas { Width = LoupeSize + 6, Height = LoupeSize + 6 };
        var background = new Shapes.Ellipse
        {
            Width = LoupeSize + 6,
            Height = LoupeSize + 6,
            Effect = Metrics.ToolbarShadow()
        };
        background.SetResourceReference(Shapes.Shape.FillProperty, Palette.Background);
        container.Children.Add(background);

        var cells = new Canvas { Width = LoupeSize, Height = LoupeSize, Clip = new EllipseGeometry(new Point(LoupeSize / 2, LoupeSize / 2), LoupeSize / 2, LoupeSize / 2) };
        RenderOptions.SetBitmapScalingMode(loupeImage, BitmapScalingMode.NearestNeighbor);
        cells.Children.Add(loupeImage);
        var grid = Palette.Frozen(0x22, 0xFA, 0xF9, 0xF5);
        for (int i = 1; i < LoupePixels; i++)
        {
            cells.Children.Add(new Shapes.Line { X1 = i * LoupeCell, X2 = i * LoupeCell, Y1 = 0, Y2 = LoupeSize, Stroke = grid, StrokeThickness = 1 });
            cells.Children.Add(new Shapes.Line { Y1 = i * LoupeCell, Y2 = i * LoupeCell, X1 = 0, X2 = LoupeSize, Stroke = grid, StrokeThickness = 1 });
        }
        loupeCenter.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
        cells.Children.Add(loupeCenter);
        Canvas.SetLeft(cells, 3);
        Canvas.SetTop(cells, 3);
        container.Children.Add(cells);

        var rim = new Shapes.Ellipse { Width = LoupeSize + 2, Height = LoupeSize + 2, StrokeThickness = 1 };
        rim.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Line);
        Canvas.SetLeft(rim, 2);
        Canvas.SetTop(rim, 2);
        container.Children.Add(rim);
        return container;
    }

    // Color under the pointer (swatch and hex) and the screen position.
    Border BuildColorChip()
    {
        swatch.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Line);
        colorText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        positionText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(colorText);
        row.Children.Add(positionText);
        return ToolWindows.Chip(row);
    }

    Border BuildHint()
    {
        hintText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        var chip = ToolWindows.Chip(hintText);
        chip.CornerRadius = new CornerRadius(10);
        chip.Padding = new Thickness(14, 8, 14, 8);
        chip.Effect = Metrics.ChipShadow();
        return chip;
    }

    void HookUpInput()
    {
        MouseMove += (_, e) => PointerMoved(DevicePosition(e));
        MouseLeftButtonDown += (_, e) => { pressedAt = DevicePosition(e); pressed = true; dragging = false; CaptureMouse(); };
        MouseLeftButtonUp += (_, e) => Released(DevicePosition(e));
        MouseRightButtonDown += (_, e) =>
        {
            if (!dragging) { Cancel(); return; }
            pressed = dragging = false;   // discard the drag
            ReleaseMouseCapture();
            PointerMoved(DevicePosition(e));
        };
        PreviewKeyDown += (_, e) => { OnKey(e.Key); e.Handled = true; };
        Deactivated += delegate { if (IsVisible) Cancel(); };
    }

    // Pointer position in whole device pixels relative to the virtual screen.
    Point DevicePosition(MouseEventArgs e)
    {
        Point position = e.GetPosition(root);
        return new Point(Math.Floor(position.X * scale), Math.Floor(position.Y * scale));
    }

    Point CursorPosition()
    {
        NativeMethods.GetCursorPos(out var cursor);
        return new Point(cursor.x - virtualScreen.X, cursor.y - virtualScreen.Y);
    }

    // ---------- Opening ----------

    void ConfigureWindow()
    {
        Title = UiText.Titles.Overlay;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Palette.Apply(Resources);
    }

    /// <summary>Dashed accent line through the pointer.</summary>
    static Shapes.Line BuildGuide() => new() { Stroke = GuideBrush, StrokeThickness = 1, StrokeDashArray = GuideDashes };

    // The frozen screen at the bottom, everything else above it and transparent to the mouse.
    void ComposeLayers()
    {
        root.Children.Add(frozenScreen);
        root.Children.Add(decorations);
        foreach (var element in new UIElement[] { dimming, horizontalGuide, verticalGuide, frame, sizeChip, loupe, colorChip, hint })
            decorations.Children.Add(element);
        Content = root;
    }

    public void Open() => Begin(recording: false);

    public void OpenForRecording() => Begin(recording: true);

    void Begin(bool recording)
    {
        if (IsVisible) return;
        forRecording = recording;
        hintText.Text = recording ? UiText.Overlay.RecordingHint : UiText.Overlay.ScreenshotHint;
        Palette.Apply(Resources);
        virtualScreen = ScreenGrabber.VirtualScreen();
        screen = ScreenGrabber.Capture(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height, 96 * scale);   // before showing anything so none of our windows are captured
        ScreenGrabber.VisibleWindows(virtualScreen, windows);
        ScreenGrabber.Monitors(monitors, workAreas);

        frozenScreen.Source = screen;
        frozenScreen.Width = root.Width = virtualScreen.Width / scale;
        frozenScreen.Height = root.Height = virtualScreen.Height / scale;
        dimming.Data = null;
        frame.Visibility = sizeChip.Visibility = Visibility.Hidden;
        pressed = dragging = false;
        selection = Rect.Empty;
        decorations.Opacity = 0;

        Point cursor = CursorPosition();
        PlaceHint(MonitorAt(cursor, monitors));
        PointerMoved(cursor);
        RevealWhenRendered();
    }

    // Centered near the bottom of the monitor with the pointer.
    void PlaceHint(RECT monitor)
    {
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(hint, ((monitor.left + monitor.right) / 2.0 - virtualScreen.X) / scale - hint.DesiredSize.Width / 2);
        Canvas.SetTop(hint, (monitor.bottom - virtualScreen.Y) / scale - 90);
    }

    // Show cloaked and let it render, then uncloak: no black flash.
    void RevealWhenRendered()
    {
        shown.Begin();
        NativeMethods.Cloak(hwnd, true);
        Width = virtualScreen.Width / scale;
        Height = virtualScreen.Height / scale;
        Show();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height, NativeMethods.SWP_SHOWWINDOW);
        ToolWindows.TakeFocus(this, hwnd);
        var reveal = new DispatcherTimer(DispatcherPriority.Render) { Interval = Motion.RevealDelay };
        reveal.Tick += delegate
        {
            reveal.Stop();
            if (!IsVisible) return;
            NativeMethods.Cloak(hwnd, false);
            decorations.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.FadeIn)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };
        reveal.Start();
    }

    // ---------- Pointer ----------

    // Highlights the window below or shows the dragged area, and moves the loupe.
    void PointerMoved(Point position)
    {
        if (screen == null) return;
        if (pressed && !dragging && (Math.Abs(position.X - pressedAt.X) > DragThreshold || Math.Abs(position.Y - pressedAt.Y) > DragThreshold)) dragging = true;
        selection = dragging ? DraggedArea(pressedAt, position, (Keyboard.Modifiers & ModifierKeys.Shift) != 0) : WindowAt(position);
        ShowSelection(selection);

        horizontalGuide.X1 = 0;
        horizontalGuide.X2 = virtualScreen.Width / scale;
        horizontalGuide.Y1 = horizontalGuide.Y2 = (position.Y + 0.5) / scale;
        verticalGuide.Y1 = 0;
        verticalGuide.Y2 = virtualScreen.Height / scale;
        verticalGuide.X1 = verticalGuide.X2 = (position.X + 0.5) / scale;
        horizontalGuide.Visibility = verticalGuide.Visibility = selection.IsEmpty || dragging ? Visibility.Visible : Visibility.Hidden;
        UpdateLoupe(position);
    }

    /// <summary>The area between the press and the pointer, both pixels included; a square with Shift.</summary>
    internal static Rect DraggedArea(Point start, Point end, bool square)
    {
        double width = Math.Abs(end.X - start.X) + 1, height = Math.Abs(end.Y - start.Y) + 1;
        if (square) width = height = Math.Max(width, height);
        return new Rect(end.X < start.X ? start.X - width + 1 : start.X, end.Y < start.Y ? start.Y - height + 1 : start.Y, width, height);
    }

    // The topmost visible window under the point, or Rect.Empty.
    Rect WindowAt(Point position)
    {
        foreach (Rect window in windows)
            if (window.Contains(position)) return window;
        return Rect.Empty;
    }

    // Dimming with a hole, frame and size chip for an area (device pixels).
    void ShowSelection(Rect area)
    {
        var everything = new RectangleGeometry(new Rect(0, 0, virtualScreen.Width / scale, virtualScreen.Height / scale));
        if (area.IsEmpty)
        {
            dimming.Data = everything;
            frame.Visibility = sizeChip.Visibility = Visibility.Hidden;
            return;
        }
        var shown = new Rect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale);
        var holes = new GeometryGroup { FillRule = FillRule.EvenOdd };
        holes.Children.Add(everything);
        holes.Children.Add(new RectangleGeometry(shown));
        dimming.Data = holes;
        frame.Width = shown.Width + 2;
        frame.Height = shown.Height + 2;
        Canvas.SetLeft(frame, shown.X - 1);
        Canvas.SetTop(frame, shown.Y - 1);
        sizeText.Text = UiText.Common.Size((int)area.Width, (int)area.Height);
        Canvas.SetLeft(sizeChip, shown.X);
        Canvas.SetTop(sizeChip, shown.Y >= 36 ? shown.Y - 34 : shown.Y + 8);   // at the top edge: inside
        frame.Visibility = sizeChip.Visibility = Visibility.Visible;
    }

    void UpdateLoupe(Point position)
    {
        var image = screen!;
        int x = (int)position.X, y = (int)position.Y, half = LoupePixels / 2;
        int left = Math.Max(0, Math.Min(image.PixelWidth - LoupePixels, x - half));
        int top = Math.Max(0, Math.Min(image.PixelHeight - LoupePixels, y - half));
        loupeImage.Source = new CroppedBitmap(image, new Int32Rect(left, top, LoupePixels, LoupePixels));
        Canvas.SetLeft(loupeCenter, (x - left) * LoupeCell - 1);
        Canvas.SetTop(loupeCenter, (y - top) * LoupeCell - 1);

        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(Math.Min(x, image.PixelWidth - 1), Math.Min(y, image.PixelHeight - 1), 1, 1), pixel, 4, 0);
        color = HexColor(pixel[2], pixel[1], pixel[0]);
        swatch.Fill = new SolidColorBrush(Color.FromRgb(pixel[2], pixel[1], pixel[0]));
        colorText.Text = color;
        positionText.Text = (x + virtualScreen.X) + ", " + (y + virtualScreen.Y);
        PlaceLoupe(position);
    }

    internal static string HexColor(byte red, byte green, byte blue) =>
        "#" + red.ToString("X2", CultureInfo.InvariantCulture) + green.ToString("X2", CultureInfo.InvariantCulture) + blue.ToString("X2", CultureInfo.InvariantCulture);

    // Below right of the pointer, flipped at the monitor edge.
    void PlaceLoupe(Point position)
    {
        RECT monitor = MonitorAt(position, monitors);
        colorChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = position.X / scale + 24, top = position.Y / scale + 24, totalHeight = LoupeSize + 12 + colorChip.DesiredSize.Height;
        if (left + LoupeSize + 6 > (monitor.right - virtualScreen.X) / scale) left = position.X / scale - 24 - LoupeSize - 6;
        if (top + totalHeight > (monitor.bottom - virtualScreen.Y) / scale) top = position.Y / scale - 24 - totalHeight;
        Canvas.SetLeft(loupe, left);
        Canvas.SetTop(loupe, top);
        Canvas.SetLeft(colorChip, left + (LoupeSize + 6 - colorChip.DesiredSize.Width) / 2);
        Canvas.SetTop(colorChip, top + LoupeSize + 12);
    }

    void Released(Point position)
    {
        if (!pressed) return;
        pressed = false;
        ReleaseMouseCapture();
        if (dragging)
        {
            dragging = false;
            if (selection.Width >= MinimumAreaSize && selection.Height >= MinimumAreaSize) Finish(selection);
            return;
        }
        // plain click: the window below, otherwise the whole monitor
        Rect window = WindowAt(position);
        Finish(window.IsEmpty ? MonitorArea(MonitorAt(position, monitors)) : window);
    }

    void OnKey(Key key)
    {
        Point cursor = CursorPosition();
        switch (key)
        {
            case Key.Escape:
                Cancel();
                break;
            case Key.Space:
                Finish(MonitorArea(MonitorAt(cursor, monitors)));
                break;
            case Key.Enter when !selection.IsEmpty && !dragging:
                Finish(selection);
                break;
            case Key.C when !forRecording:
                CopyColor(color);
                break;
            case Key.LeftShift:
            case Key.RightShift:
                PointerMoved(cursor);   // switch between rectangle and square right away
                break;
        }
    }

    // ---------- Result ----------

    Rect MonitorArea(RECT monitor) => new(monitor.left - virtualScreen.X, monitor.top - virtualScreen.Y, monitor.Width, monitor.Height);

    void Finish(Rect area)
    {
        area.Intersect(new Rect(0, 0, virtualScreen.Width, virtualScreen.Height));
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) { Cancel(); return; }
        var crop = new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);   // the card goes on the monitor with the area
        RECT monitor = MonitorAt(center, monitors), workArea = MonitorAt(center, workAreas);
        if (forRecording)
        {
            Dismiss();
            Recorded?.Invoke(new Int32Rect(crop.X + virtualScreen.X, crop.Y + virtualScreen.Y, crop.Width, crop.Height), monitor, workArea);
            return;
        }
        var image = new CroppedBitmap(screen!, crop);
        image.Freeze();
        var capture = new ScreenCapture
        {
            FullScreen = screen!,
            Image = image,
            Crop = crop,
            Monitor = monitor,
            WorkArea = workArea,
            VirtualLeft = virtualScreen.X,
            VirtualTop = virtualScreen.Y,
            VirtualWidth = virtualScreen.Width,
            VirtualHeight = virtualScreen.Height,
            Scale = scale
        };
        Dismiss();
        captured!(capture);
    }

    void CopyColor(string color) => CopyColorAsync(color).OnFailure("Copying a color", () => { if (IsVisible) colorText.Text = UiText.Common.CopyFailed; });

    async Task CopyColorAsync(string color)
    {
        bool copied = await ClipboardWriter.SetTextAsync(color);
        if (IsVisible) colorText.Text = copied ? color + UiText.Overlay.ColorCopiedSuffix : UiText.Common.CopyFailed;
    }

    /// <summary>Closes the overlay; while picking a recording area this reports "aborted".</summary>
    internal void Cancel()
    {
        Dismiss();
        if (forRecording) Aborted?.Invoke();
    }

    void Dismiss()
    {
        shown.End();
        NativeMethods.Cloak(hwnd, true);
        pressed = dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Hide();
        frozenScreen.Source = loupeImage.Source = null;
        screen = null;   // release the large image
    }

    // The entry of `list` for the monitor containing the point; the first monitor if none does.
    RECT MonitorAt(Point position, List<RECT> list)
    {
        double x = position.X + virtualScreen.X, y = position.Y + virtualScreen.Y;
        for (int i = 0; i < monitors.Count; i++)
        {
            RECT monitor = monitors[i];
            if (x >= monitor.left && x < monitor.right && y >= monitor.top && y < monitor.bottom) return list[i];
        }
        return list.Count > 0
            ? list[0]
            : new RECT { left = virtualScreen.X, top = virtualScreen.Y, right = virtualScreen.X + virtualScreen.Width, bottom = virtualScreen.Y + virtualScreen.Height };
    }
}
