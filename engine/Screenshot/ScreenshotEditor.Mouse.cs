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
        mode = Mode.None;
        discardArmed = false;
        pointer = Offscreen;
        frozenImage.Source = screenshot.FullScreen;
        root.Width = frozenImage.Width = screenshot.VirtualWidth / scale;
        root.Height = frozenImage.Height = screenshot.VirtualHeight / scale;
        crop = new Rect(screenshot.Crop.X / scale, screenshot.Crop.Y / scale, screenshot.Crop.Width / scale, screenshot.Crop.Height / scale);
        notice.Visibility = tooltip.Visibility = Visibility.Hidden;
        SetTool(tool);
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
        if (openMenuGroup != null) { CloseMenu(); return; }
        if (textBox != null) { CommitText(); return; }
        discardArmed = false;
        pressPoint = position;
        if (tool == Tool.Move)
        {
            StartMoving(position);
            return;
        }
        if (!crop.Contains(position)) return;
        if (tool is Tool.Text or Tool.Label) { StartTextInput(position, tool == Tool.Label); return; }
        if (tool == Tool.Number) { PlaceNumber(position); UpdateNumberPreview(); return; }
        StartStroke(position);
    }

    // Move tool: drag a markup item, or the whole crop when clicking inside it.
    void StartMoving(Point position)
    {
        var hit = ItemAt(position);
        if (hit != null)
        {
            mode = Mode.MoveItem;
            selectedItem = hit;
            itemOffset = (TranslateTransform)hit.RenderTransform;
            itemStartX = itemOffset.X;
            itemStartY = itemOffset.Y;
        }
        else if (crop.Contains(position))
        {
            mode = Mode.MoveCrop;
            cropAtPress = crop;
            selectedItem = null;
        }
        else return;
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
        if (tool == Tool.Arrow) stroke.Fill = brush;
        else if (tool == Tool.Marker)
        {
            stroke.Stroke = MarkerBrush();
            stroke.StrokeThickness = style.MarkerWidth;
        }
        else if (tool is Tool.Pixel or Tool.Blur or Tool.Spot)
        {
            // only a dashed frame; the result is created on release
            stroke.StrokeThickness = 1.5;
            stroke.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
            stroke.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Text);
        }
        snapped = false;
        if (tool is Tool.Pen or Tool.Marker)
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
        mode = Mode.Draw;
        CaptureMouse();
    }

    SolidColorBrush MarkerBrush()
    {
        return new SolidColorBrush(style.MarkerInk);
    }

    internal void PointerMoved(Point position)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (mode)
        {
            case Mode.None:
                pointer = position;
                UpdateCursor(position);
                break;
            case Mode.MoveItem:
                itemOffset.X = itemStartX + position.X - pressPoint.X;
                itemOffset.Y = itemStartY + position.Y - pressPoint.Y;
                if (SpotTag.Equals(selectedItem!.Tag)) RefreshSpotlightShade(null);
                break;
            case Mode.MoveCrop:
                MoveCrop(position);
                break;
            case Mode.ResizeCrop:
                ResizeCrop(position);
                break;
            case Mode.Draw:
                Drag(KeepInCrop(position), shift);
                break;
        }
    }

    void UpdateCursor(Point position)
    {
        if (UpdateNumberPreview()) Cursor = Cursors.None;   // the preview is the pointer
        else if (tool == Tool.Move) Cursor = ItemAt(position) != null || crop.Contains(position) ? Cursors.SizeAll : Cursors.Arrow;
        else Cursor = !crop.Contains(position) ? Cursors.Arrow : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
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
        switch (tool)
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
                if (tool == Tool.Ellipse) { stroke!.Data = new EllipseGeometry(new Rect(pressPoint, position)); break; }
                double radius = tool == Tool.Box ? 6 : tool == Tool.Spot ? 8 : 0;
                stroke!.Data = new RectangleGeometry(new Rect(pressPoint, position), radius, radius);
                if (tool == Tool.Spot) RefreshSpotlightShade(new Rect(pressPoint, position));   // show the spotlight while dragging
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
            if (tool == Tool.Marker) position = new Point(position.X, pressPoint.Y);
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
