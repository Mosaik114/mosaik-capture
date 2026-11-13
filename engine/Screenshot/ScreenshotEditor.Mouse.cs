namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: opening, layout and pointer handling (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    // ---------- Open / layout ----------

    public void Open(ScreenCapture screenshot)
    {
        shown.Begin();
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
        ClearMarkup();
        document.Reset(new Rect(screenshot.Crop.X / screenshot.Scale, screenshot.Crop.Y / screenshot.Scale,
            screenshot.Crop.Width / screenshot.Scale, screenshot.Crop.Height / screenshot.Scale));
        textBox = null;
        textHost = null;
        CloseMenu();
        interaction.Reset();
        interaction.DisarmDiscard();
        pointer = Offscreen;
        frozenImage.Source = screenshot.FullScreen;
        root.Width = frozenImage.Width = screenshot.VirtualWidth / scale;
        root.Height = frozenImage.Height = screenshot.VirtualHeight / scale;
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

    Int32Rect CropInPixels() => CropGeometry.InPixels(document.Crop, scale, capture.FullScreen.PixelWidth, capture.FullScreen.PixelHeight);

    /// <summary>Fits dimming, frame, handles, size chip and toolbar to the document.Crop.</summary>
    internal void LayoutCrop()
    {
        var holes = new GeometryGroup { FillRule = FillRule.EvenOdd };
        holes.Children.Add(new RectangleGeometry(new Rect(0, 0, root.Width, root.Height)));
        holes.Children.Add(new RectangleGeometry(document.Crop));
        dimming.Data = holes;
        markup.Clip = new RectangleGeometry(document.Crop);
        frame.Width = document.Crop.Width + 2;
        frame.Height = document.Crop.Height + 2;
        Canvas.SetLeft(frame, document.Crop.X - 1);
        Canvas.SetTop(frame, document.Crop.Y - 1);
        for (int i = 0; i < resizeHandles.Length; i++)
        {
            Point center = CropGeometry.HandleCenter(document.Crop, i);
            Canvas.SetLeft(resizeHandles[i], center.X - Metrics.HandleHitSize / 2);
            Canvas.SetTop(resizeHandles[i], center.Y - Metrics.HandleHitSize / 2);
        }
        Int32Rect pixels = CropInPixels();
        sizeText.Text = UiText.Common.Size(pixels.Width, pixels.Height);
        Rect monitor = MonitorArea();
        Canvas.SetLeft(sizeChip, document.Crop.X);
        Canvas.SetTop(sizeChip, CropGeometry.SizeChipTop(document.Crop, monitor));
        PlaceToolbar(monitor);
    }

    // Below the document.Crop; no room below: above, otherwise inside.
    void PlaceToolbar(Rect monitor)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point position = CropGeometry.ToolbarPosition(document.Crop, toolbar.DesiredSize, monitor);
        Canvas.SetLeft(toolbar, position.X);
        Canvas.SetTop(toolbar, position.Y);
    }

    // ---------- Pointer ----------

    internal void PointerDown(Point position)
    {
        var hit = interaction.Tool == Tool.Move ? ItemAt(position) : null;
        var action = interaction.Press(openMenuGroup != null, document.Typing, hit != null, document.Crop.Contains(position));
        if (action == PressAction.CloseMenu) { CloseMenu(); return; }
        if (action == PressAction.CommitText) { CommitText(); return; }
        interaction.DisarmDiscard();
        interaction.PressPoint = position;
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
        var item = itemsByElement[hit];
        document.Selected = item;
        interaction.ItemStart = item.Offset;
        CaptureMouse();
    }

    // ...or the whole document.Crop when pressing inside it.
    void StartMovingCrop()
    {
        interaction.Begin(PointerMode.MoveCrop);
        interaction.CropAtPress = document.Crop;
        document.Selected = null;
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
        snap.Reset();
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
                document.MoveTo(document.Selected!, interaction.ItemStart + (position - interaction.PressPoint));
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
        else if (interaction.Tool == Tool.Move) Cursor = ItemAt(position) != null || document.Crop.Contains(position) ? Cursors.SizeAll : Cursors.Arrow;
        else Cursor = !document.Crop.Contains(position) ? Cursors.Arrow : interaction.Tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    Size SurfaceSize => new(root.Width, root.Height);

    void MoveCrop(Point position)
    {
        document.SetCrop(CropGeometry.Moved(interaction.CropAtPress, position - interaction.PressPoint, SurfaceSize, scale));
    }

    void ResizeCrop(Point position)
    {
        document.SetCrop(CropGeometry.Resized(interaction.CropAtPress, interaction.Handle, position, SurfaceSize, scale));
    }

    Point KeepInCrop(Point position) => CropGeometry.KeepInside(document.Crop, position);

    void Drag(Point position, bool shift)
    {
        switch (interaction.Tool)
        {
            case Tool.Arrow:
                if (shift) position = SnapTo45Degrees(interaction.PressPoint, position);
                stroke!.Data = ShapeRecognizer.Arrow(interaction.PressPoint, position, style.StrokeWidth);
                break;
            case Tool.Line:
                if (shift) position = SnapTo45Degrees(interaction.PressPoint, position);
                stroke!.Data = new LineGeometry(interaction.PressPoint, position);
                break;
            case Tool.Box:
            case Tool.Ellipse:
            case Tool.Pixel:
            case Tool.Blur:
            case Tool.Spot:
                if (shift) position = SquareFrom(interaction.PressPoint, position);
                if (interaction.Tool == Tool.Ellipse) { stroke!.Data = new EllipseGeometry(new Rect(interaction.PressPoint, position)); break; }
                double radius = interaction.Tool == Tool.Box ? 6 : interaction.Tool == Tool.Spot ? 8 : 0;
                stroke!.Data = new RectangleGeometry(new Rect(interaction.PressPoint, position), radius, radius);
                if (interaction.Tool == Tool.Spot) RefreshSpotlightShade(new Rect(interaction.PressPoint, position));   // show the spotlight while dragging
                break;
            case Tool.Pen:
            case Tool.Marker:
                DrawFreehand(position, shift);
                break;
        }
    }

    void DrawFreehand(Point position, bool shift)
    {
        if (snap.Snapped) { Reshape(position); return; }
        if (shift)
        {
            // Shift = straight line, horizontal for the highlighter
            strokeLine.Points.Clear();
            if (interaction.Tool == Tool.Marker) position = new Point(position.X, interaction.PressPoint.Y);
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

    Point SnapTo45Degrees(Point start, Point end) => CropGeometry.SnapTo45Degrees(start, end, document.Crop);

    Point SquareFrom(Point start, Point end) => CropGeometry.SquareCorner(start, end, document.Crop);

    List<Point> StrokePoints(Shapes.Path path)
    {
        var figure = ((PathGeometry)path.Data).Figures[0];
        var points = new List<Point> { figure.StartPoint };
        points.AddRange(strokeLine.Points);
        return points;
    }
}
