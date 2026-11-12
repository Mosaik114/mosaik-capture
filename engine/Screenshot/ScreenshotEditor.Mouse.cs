namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: opening, layout and pointer handling (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    const double MinCropSize = 8;

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
        undo.Clear();
        redo.Clear();
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
        SetColor(colorIndex);
        SetWidth(widthIndex);
        LayoutCrop();
    }

    // The monitor of the screenshot in WPF units relative to the window.
    Rect MonitorArea()
    {
        var monitor = capture.Monitor;
        return new Rect((monitor.left - capture.VirtualLeft) / scale, (monitor.top - capture.VirtualTop) / scale, monitor.Width / scale, monitor.Height / scale);
    }

    double SnapToPixel(double value) => Math.Round(value * scale) / scale;

    Int32Rect CropInPixels()
    {
        int x = (int)Math.Round(crop.X * scale), y = (int)Math.Round(crop.Y * scale);
        int width = Math.Min((int)Math.Round(crop.Width * scale), capture.FullScreen.PixelWidth - x);
        int height = Math.Min((int)Math.Round(crop.Height * scale), capture.FullScreen.PixelHeight - y);
        return new Int32Rect(x, y, Math.Max(1, width), Math.Max(1, height));
    }

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
            Canvas.SetLeft(resizeHandles[i], crop.X + crop.Width * HandleX[i] - 9.5);
            Canvas.SetTop(resizeHandles[i], crop.Y + crop.Height * HandleY[i] - 9.5);
        }
        Int32Rect pixels = CropInPixels();
        sizeText.Text = UiText.Common.Size(pixels.Width, pixels.Height);
        Rect monitor = MonitorArea();
        Canvas.SetLeft(sizeChip, crop.X);
        Canvas.SetTop(sizeChip, crop.Y - 34 >= monitor.Top + 4 ? crop.Y - 34 : crop.Y + 10);
        PlaceToolbar(monitor);
    }

    // Below the crop; no room below: above, otherwise inside.
    void PlaceToolbar(Rect monitor)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = toolbar.DesiredSize.Width, height = toolbar.DesiredSize.Height;
        double x = Math.Max(monitor.Left + 12, Math.Min(monitor.Right - 12 - width, crop.X + (crop.Width - width) / 2));
        double y = crop.Bottom + 14;
        if (y + height + 50 > monitor.Bottom) y = crop.Top - 14 - height >= monitor.Top + 50 ? crop.Top - 14 - height : crop.Bottom - height - 24;
        y = Math.Max(monitor.Top + 12, Math.Min(monitor.Bottom - height - 12, y));
        Canvas.SetLeft(toolbar, x);
        Canvas.SetTop(toolbar, y);
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
        var brush = new SolidColorBrush(InkColors[colorIndex]);
        stroke = new Shapes.Path
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = brush,
            StrokeThickness = StrokeWidths[widthIndex],
            RenderTransform = new TranslateTransform()
        };
        if (tool == Tool.Arrow) stroke.Fill = brush;
        else if (tool == Tool.Marker)
        {
            stroke.Stroke = MarkerBrush();
            stroke.StrokeThickness = MarkerWidths[widthIndex];
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
        Color ink = InkColors[colorIndex];
        return new SolidColorBrush(Color.FromArgb(0x66, ink.R, ink.G, ink.B));
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

    void MoveCrop(Point position)
    {
        double x = Math.Max(0, Math.Min(root.Width - cropAtPress.Width, cropAtPress.X + position.X - pressPoint.X));
        double y = Math.Max(0, Math.Min(root.Height - cropAtPress.Height, cropAtPress.Y + position.Y - pressPoint.Y));
        crop = new Rect(SnapToPixel(x), SnapToPixel(y), cropAtPress.Width, cropAtPress.Height);
        LayoutCrop();
    }

    void ResizeCrop(Point position)
    {
        double left = cropAtPress.Left, top = cropAtPress.Top, right = cropAtPress.Right, bottom = cropAtPress.Bottom;
        double x = Math.Max(0, Math.Min(root.Width, position.X)), y = Math.Max(0, Math.Min(root.Height, position.Y));
        if (HandleX[activeHandle] == 0) left = Math.Min(x, right - MinCropSize);
        else if (HandleX[activeHandle] == 1) right = Math.Max(x, left + MinCropSize);
        if (HandleY[activeHandle] == 0) top = Math.Min(y, bottom - MinCropSize);
        else if (HandleY[activeHandle] == 1) bottom = Math.Max(y, top + MinCropSize);
        crop = new Rect(SnapToPixel(left), SnapToPixel(top), SnapToPixel(right) - SnapToPixel(left), SnapToPixel(bottom) - SnapToPixel(top));
        LayoutCrop();
    }

    Point KeepInCrop(Point position) =>
        new(Math.Max(crop.Left, Math.Min(crop.Right, position.X)), Math.Max(crop.Top, Math.Min(crop.Bottom, position.Y)));

    void Drag(Point position, bool shift)
    {
        switch (tool)
        {
            case Tool.Arrow:
                if (shift) position = SnapTo45Degrees(pressPoint, position);
                stroke!.Data = ShapeRecognizer.Arrow(pressPoint, position, StrokeWidths[widthIndex]);
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

    // Same length, direction rounded to 45 degree steps, kept in the crop.
    Point SnapTo45Degrees(Point start, Point end)
    {
        double angle = Math.Round(Math.Atan2(end.Y - start.Y, end.X - start.X) / (Math.PI / 4)) * (Math.PI / 4), length = (end - start).Length;
        return KeepInCrop(new Point(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle)));
    }

    // The corner of a square with the larger side, kept in the crop.
    Point SquareFrom(Point start, Point end)
    {
        double width = end.X - start.X, height = end.Y - start.Y, side = Math.Max(Math.Abs(width), Math.Abs(height));
        return KeepInCrop(new Point(start.X + (width < 0 ? -side : side), start.Y + (height < 0 ? -side : side)));
    }

    List<Point> StrokePoints(Shapes.Path path)
    {
        var figure = ((PathGeometry)path.Data).Figures[0];
        var points = new List<Point> { figure.StartPoint };
        points.AddRange(strokeLine.Points);
        return points;
    }
}
