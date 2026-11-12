namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: opening, layout and pointer handling (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    // ---------- Open / layout ----------

    public void Open(ScreenCapture screenshot)
    {
        Load(screenshot);
        NativeMethods.Cloak(hwnd, true);   // finish rendering first, then reveal
        Width = root.Width;
        Height = root.Height;
        Show();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, screenshot.VirtualLeft, screenshot.VirtualTop, screenshot.VirtualWidth, screenshot.VirtualHeight, NativeMethods.SWP_SHOWWINDOW);
        ToolWindows.TakeFocus(this, hwnd);
        UpdateLayout();
        LayoutCrop();
        var reveal = new DispatcherTimer(DispatcherPriority.Render) { Interval = Motion.RevealDelay };
        reveal.Tick += delegate { reveal.Stop(); if (IsVisible) NativeMethods.Cloak(hwnd, false); };
        reveal.Start();
    }

    /// <summary>Resets the editor and shows the screenshot in it, without showing the window (pixel tests use this directly).</summary>
    internal void Load(ScreenCapture screenshot)
    {
        capture = screenshot;
        scale = screenshot.Scale;
        Palette.Apply(Resources);
        markup.Children.Clear();
        history.Clear();
        textBox = null;
        textHost = null;
        CloseMenu();
        selectedItem = null;
        interaction.Reset();
        discardArmed = false;
        pointer = Offscreen;
        frozenImage.Source = screenshot.FullScreen;
        root.Width = frozenImage.Width = screenshot.VirtualWidth / scale;
        root.Height = frozenImage.Height = screenshot.VirtualHeight / scale;
        crop = new Rect(screenshot.Crop.X / scale, screenshot.Crop.Y / scale, screenshot.Crop.Width / scale, screenshot.Crop.Height / scale);
        notice.Visibility = tooltip.Visibility = Visibility.Hidden;
        SetTool(interaction.Tool);
        SetColor(style.ColorIndex);
        SetWidth(style.WidthIndex);
        LayoutCrop();
    }

    // The monitor of the screenshot in WPF units relative to the window.
    Rect MonitorArea()
    {
        var monitor = capture.Monitor;
        return new Rect((monitor.left - capture.VirtualLeft) / scale, (monitor.top - capture.VirtualTop) / scale, monitor.Width / scale, monitor.Height / scale);
    }

    Int32Rect CropInPixels() => CropGeometry.InPixels(crop, scale, capture.FullScreen.PixelWidth, capture.FullScreen.PixelHeight);

    /// <summary>Fits dimming, frame, handles, size chip and toolbar to the crop.</summary>
    internal void LayoutCrop()
    {
        var holes = new GeometryGroup { FillRule = FillRule.EvenOdd };
        holes.Children.Add(new RectangleGeometry(new Rect(0, 0, root.Width, root.Height)));
        holes.Children.Add(new RectangleGeometry(crop));
        dimming.Data = holes;
        markup.Clip = new RectangleGeometry(crop);
        frame.Width = crop.Width + 2;
        frame.Height = crop.Height + 2;
        Canvas.SetLeft(frame, crop.X - 1);
        Canvas.SetTop(frame, crop.Y - 1);
        for (int i = 0; i < resizeHandles.Length; i++)
        {
            Point center = CropGeometry.HandleCenter(crop, i);
            Canvas.SetLeft(resizeHandles[i], center.X - Metrics.HandleHitSize / 2);
            Canvas.SetTop(resizeHandles[i], center.Y - Metrics.HandleHitSize / 2);
        }
        Int32Rect pixels = CropInPixels();
        sizeText.Text = UiText.Common.Size(pixels.Width, pixels.Height);
        Rect monitor = MonitorArea();
        Canvas.SetLeft(sizeChip, crop.X);
        Canvas.SetTop(sizeChip, CropGeometry.SizeChipTop(crop, monitor));
        PlaceToolbar(monitor);
    }

    // Below the crop; no room below: above, otherwise inside.
    void PlaceToolbar(Rect monitor)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point position = CropGeometry.ToolbarPosition(crop, toolbar.DesiredSize, monitor);
        Canvas.SetLeft(toolbar, position.X);
        Canvas.SetTop(toolbar, position.Y);
    }

    // ---------- Pointer ----------

    internal void PointerDown(Point position)
    {
        var hit = interaction.Tool == Tool.Move ? ItemAt(position) : null;
        var action = interaction.Press(openMenuGroup != null, textBox != null, hit != null, crop.Contains(position));
        if (action == PressAction.CloseMenu) { CloseMenu(); return; }
        if (action == PressAction.CommitText) { CommitText(); return; }
        discardArmed = false;
        pressPoint = position;
        switch (action)
        {
            case PressAction.MoveItem:
                StartMovingItem(hit!);
                break;
            case PressAction.MoveCrop:
                StartMovingCrop();
                break;
            case PressAction.StartText:
                StartTextInput(position, interaction.Tool == Tool.Label);
                break;
            case PressAction.PlaceNumber:
                PlaceNumber(position);
                UpdateNumberPreview();
                break;
            case PressAction.StartStroke:
                StartStroke(position);
                break;
        }
    }

    // Move tool: drag a markup item...
    void StartMovingItem(FrameworkElement hit)
    {
        interaction.Begin(PointerMode.MoveItem);
        selectedItem = hit;
        itemOffset = (TranslateTransform)hit.RenderTransform;
        itemStartX = itemOffset.X;
        itemStartY = itemOffset.Y;
        CaptureMouse();
    }

    // ...or the whole crop when pressing inside it.
    void StartMovingCrop()
    {
        interaction.Begin(PointerMode.MoveCrop);
        cropAtPress = crop;
        selectedItem = null;
        CaptureMouse();
    }

    void StartStroke(Point position)
    {
        var brush = new SolidColorBrush(style.Ink);
        stroke = new Shapes.Path
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = brush,
            StrokeThickness = style.StrokeWidth,
            RenderTransform = new TranslateTransform()
        };
        if (interaction.Tool == Tool.Arrow) stroke.Fill = brush;
        else if (interaction.Tool == Tool.Marker)
        {
            stroke.Stroke = MarkerBrush();
            stroke.StrokeThickness = style.MarkerWidth;
        }
        else if (interaction.Tool is Tool.Pixel or Tool.Blur or Tool.Spot)
        {
            // only a dashed frame; the result is created on release
            stroke.StrokeThickness = 1.5;
            stroke.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
            stroke.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Text);
        }
        snapped = false;
        if (interaction.Tool is Tool.Pen or Tool.Marker)
        {
            strokeLine = new PolyLineSegment();
            var figure = new PathFigure { StartPoint = position, IsFilled = false };
            figure.Segments.Add(strokeLine);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            stroke.Data = geometry;
            holdPoint = position;
        }
        markup.Children.Add(stroke);
        interaction.Begin(PointerMode.Draw);
        CaptureMouse();
    }

    SolidColorBrush MarkerBrush()
    {
        return new SolidColorBrush(style.MarkerInk);
    }

    internal void PointerMoved(Point position)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (interaction.Mode)
        {
            case PointerMode.None:
                pointer = position;
                UpdateCursor(position);
                break;
            case PointerMode.MoveItem:
                itemOffset.X = itemStartX + position.X - pressPoint.X;
                itemOffset.Y = itemStartY + position.Y - pressPoint.Y;
                if (SpotTag.Equals(selectedItem!.Tag)) RefreshSpotlightShade(null);
                break;
            case PointerMode.MoveCrop:
                MoveCrop(position);
                break;
            case PointerMode.ResizeCrop:
                ResizeCrop(position);
                break;
            case PointerMode.Draw:
                Drag(KeepInCrop(position), shift);
                break;
        }
    }

    void UpdateCursor(Point position)
    {
        if (UpdateNumberPreview()) Cursor = Cursors.None;   // the preview is the pointer
        else if (interaction.Tool == Tool.Move) Cursor = ItemAt(position) != null || crop.Contains(position) ? Cursors.SizeAll : Cursors.Arrow;
        else Cursor = !crop.Contains(position) ? Cursors.Arrow : interaction.Tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    Size SurfaceSize => new(root.Width, root.Height);

    void MoveCrop(Point position)
    {
        crop = CropGeometry.Moved(cropAtPress, position - pressPoint, SurfaceSize, scale);
        LayoutCrop();
    }

    void ResizeCrop(Point position)
    {
        crop = CropGeometry.Resized(cropAtPress, activeHandle, position, SurfaceSize, scale);
        LayoutCrop();
    }

    Point KeepInCrop(Point position) => CropGeometry.KeepInside(crop, position);

    void Drag(Point position, bool shift)
    {
        switch (interaction.Tool)
        {
            case Tool.Arrow:
                if (shift) position = SnapTo45Degrees(pressPoint, position);
                stroke!.Data = ShapeRecognizer.Arrow(pressPoint, position, style.StrokeWidth);
                break;
            case Tool.Line:
                if (shift) position = SnapTo45Degrees(pressPoint, position);
                stroke!.Data = new LineGeometry(pressPoint, position);
                break;
            case Tool.Box:
            case Tool.Ellipse:
            case Tool.Pixel:
            case Tool.Blur:
            case Tool.Spot:
                if (shift) position = SquareFrom(pressPoint, position);
                if (interaction.Tool == Tool.Ellipse) { stroke!.Data = new EllipseGeometry(new Rect(pressPoint, position)); break; }
                double radius = interaction.Tool == Tool.Box ? 6 : interaction.Tool == Tool.Spot ? 8 : 0;
                stroke!.Data = new RectangleGeometry(new Rect(pressPoint, position), radius, radius);
                if (interaction.Tool == Tool.Spot) RefreshSpotlightShade(new Rect(pressPoint, position));   // show the spotlight while dragging
                break;
            case Tool.Pen:
            case Tool.Marker:
                DrawFreehand(position, shift);
                break;
        }
    }

    void DrawFreehand(Point position, bool shift)
    {
        if (snapped) { Reshape(position); return; }
        if (shift)
        {
            // Shift = straight line, horizontal for the highlighter
            strokeLine.Points.Clear();
            if (interaction.Tool == Tool.Marker) position = new Point(position.X, pressPoint.Y);
        }
        strokeLine.Points.Add(position);
        if ((position - holdPoint).Length > 2 || shift)
        {
            // moved: restart the hold timer
            holdPoint = position;
            holdTimer.Stop();
            if (!shift) holdTimer.Start();
        }
    }

    Point SnapTo45Degrees(Point start, Point end) => CropGeometry.SnapTo45Degrees(start, end, crop);

    Point SquareFrom(Point start, Point end) => CropGeometry.SquareCorner(start, end, crop);

    List<Point> StrokePoints(Shapes.Path path)
    {
        var figure = ((PathGeometry)path.Data).Figures[0];
        var points = new List<Point> { figure.StartPoint };
        points.AddRange(strokeLine.Points);
        return points;
    }
}
