namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: opening, layout and mouse handling (fields and construction in ShotEditor.cs).
partial class ShotEditor
{
    // ---------- Open / layout ----------

    public void Open(ShotData d)
    {
        data = d;
        s = d.Scale;
        Palette.Apply(Resources);
        ink.Children.Clear();
        undo.Clear();
        redo.Clear();
        typing = null;
        typingHost = null;
        CloseMenu();
        picked = null;
        mode = Mode.None;
        escArmed = false;
        hover = new Point(-1e6, -1e6);
        back.Source = d.Full;
        root.Width = back.Width = d.Vw / s;
        root.Height = back.Height = d.Vh / s;
        sel = new Rect(d.Cut.X / s, d.Cut.Y / s, d.Cut.Width / s, d.Cut.Height / s);
        note.Visibility = tip.Visibility = Visibility.Hidden;
        SetTool(tool);
        SetColor(color);
        SetWidth(width);
        Layout();

        ShotOverlay.Cloak(hwnd, true);   // finish rendering first, then reveal
        Width = root.Width;
        Height = root.Height;
        Show();
        SetWindowPos(hwnd, (IntPtr)(-1), d.Vx, d.Vy, d.Vw, d.Vh, 0x0040);
        ShotOverlay.TakeFocus(this, hwnd);
        UpdateLayout();
        Layout();
        var reveal = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        reveal.Tick += delegate { reveal.Stop(); if (IsVisible) ShotOverlay.Cloak(hwnd, false); };
        reveal.Start();
    }

    Rect Mon()
    {
        var m = data.Monitor;
        return new Rect((m.left - data.Vx) / s, (m.top - data.Vy) / s, (m.right - m.left) / s, (m.bottom - m.top) / s);
    }

    double Snap(double v) { return Math.Round(v * s) / s; }

    Int32Rect Cut()
    {
        int x = (int)Math.Round(sel.X * s), y = (int)Math.Round(sel.Y * s);
        int w = Math.Min((int)Math.Round(sel.Width * s), data.Full.PixelWidth - x), h = Math.Min((int)Math.Round(sel.Height * s), data.Full.PixelHeight - y);
        return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    // Fits dimming, frame, handles, size chip and toolbar to the crop.
    void Layout()
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, root.Width, root.Height)));
        g.Children.Add(new RectangleGeometry(sel));
        dim.Data = g;
        ink.Clip = new RectangleGeometry(sel);
        frame.Width = sel.Width + 2;
        frame.Height = sel.Height + 2;
        Canvas.SetLeft(frame, sel.X - 1);
        Canvas.SetTop(frame, sel.Y - 1);
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(handles[i], sel.X + sel.Width * HX[i] - 9.5);
            Canvas.SetTop(handles[i], sel.Y + sel.Height * HY[i] - 9.5);
        }
        Int32Rect c = Cut();
        sizeText.Text = c.Width + " × " + c.Height;
        Rect m = Mon();
        Canvas.SetLeft(sizeChip, sel.X);
        Canvas.SetTop(sizeChip, sel.Y - 34 >= m.Top + 4 ? sel.Y - 34 : sel.Y + 10);

        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = bar.DesiredSize.Width, bh = bar.DesiredSize.Height;
        double x = Math.Max(m.Left + 12, Math.Min(m.Right - 12 - bw, sel.X + (sel.Width - bw) / 2));
        double y = sel.Bottom + 14;
        if (y + bh + 50 > m.Bottom) y = sel.Top - 14 - bh >= m.Top + 50 ? sel.Top - 14 - bh : sel.Bottom - bh - 24;   // no room below: above, otherwise inside
        y = Math.Max(m.Top + 12, Math.Min(m.Bottom - bh - 12, y));
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
    }

    // ---------- Mouse ----------

    void Down(Point p)
    {
        if (menuFor != null) { CloseMenu(); return; }
        if (typing != null) { Commit(); return; }
        escArmed = false;
        p0 = p;
        if (tool == Tool.Move)
        {
            var hit = ItemAt(p);
            if (hit != null)
            {
                mode = Mode.Item;
                picked = hit;
                itemMove = (TranslateTransform)hit.RenderTransform;
                itemX0 = itemMove.X;
                itemY0 = itemMove.Y;
            }
            else if (sel.Contains(p)) { mode = Mode.Frame; sel0 = sel; picked = null; }
            else return;
            CaptureMouse();
            return;
        }
        if (!sel.Contains(p)) return;
        if (tool == Tool.Text || tool == Tool.Label) { StartText(p, tool == Tool.Label); return; }
        if (tool == Tool.Number) { PlaceNumber(p); UpdateGhost(); return; }

        var brush = new SolidColorBrush(Inks[color]);
        cur = new Shapes.Path
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = brush,
            StrokeThickness = Widths[width],
            RenderTransform = new TranslateTransform()
        };
        if (tool == Tool.Arrow) cur.Fill = brush;
        else if (tool == Tool.Marker)
        {
            cur.Stroke = MarkerBrush();
            cur.StrokeThickness = Markers[width];
        }
        else if (tool == Tool.Pixel || tool == Tool.Blur || tool == Tool.Spot)
        {
            // only a dashed frame; the result is created on release
            cur.StrokeThickness = 1.5;
            cur.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
            cur.SetResourceReference(Shapes.Shape.StrokeProperty, "Text");
        }
        snapped = false;
        if (tool == Tool.Pen || tool == Tool.Marker)
        {
            line = new PolyLineSegment();
            var fig = new PathFigure { StartPoint = p, IsFilled = false };
            fig.Segments.Add(line);
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            cur.Data = pg;
            holdAt = p;
        }
        ink.Children.Add(cur);
        mode = Mode.Draw;
        CaptureMouse();
    }

    SolidColorBrush MarkerBrush()
    {
        return new SolidColorBrush(Color.FromArgb(0x66, Inks[color].R, Inks[color].G, Inks[color].B));
    }

    void Moved(Point p)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (mode)
        {
            case Mode.None:
                hover = p;
                if (UpdateGhost()) Cursor = Cursors.None;   // the preview is the pointer
                else if (tool == Tool.Move) Cursor = ItemAt(p) != null || sel.Contains(p) ? Cursors.SizeAll : Cursors.Arrow;
                else Cursor = !sel.Contains(p) ? Cursors.Arrow : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                break;
            case Mode.Item:
                itemMove.X = itemX0 + p.X - p0.X;
                itemMove.Y = itemY0 + p.Y - p0.Y;
                if ("spot".Equals(picked!.Tag)) RefreshShade(null);
                break;
            case Mode.Frame:
                {
                    double x = Math.Max(0, Math.Min(root.Width - sel0.Width, sel0.X + p.X - p0.X));
                    double y = Math.Max(0, Math.Min(root.Height - sel0.Height, sel0.Y + p.Y - p0.Y));
                    sel = new Rect(Snap(x), Snap(y), sel0.Width, sel0.Height);
                    Layout();
                    break;
                }
            case Mode.Resize:
                {
                    double l = sel0.Left, t = sel0.Top, r = sel0.Right, b = sel0.Bottom;
                    double px = Math.Max(0, Math.Min(root.Width, p.X)), py = Math.Max(0, Math.Min(root.Height, p.Y));
                    if (HX[handle] == 0) l = Math.Min(px, r - 8); else if (HX[handle] == 1) r = Math.Max(px, l + 8);
                    if (HY[handle] == 0) t = Math.Min(py, b - 8); else if (HY[handle] == 1) b = Math.Max(py, t + 8);
                    sel = new Rect(Snap(l), Snap(t), Snap(r) - Snap(l), Snap(b) - Snap(t));
                    Layout();
                    break;
                }
            case Mode.Draw:
                Drag(Inside(p), shift);
                break;
        }
    }

    Point Inside(Point p)
    {
        return new Point(Math.Max(sel.Left, Math.Min(sel.Right, p.X)), Math.Max(sel.Top, Math.Min(sel.Bottom, p.Y)));
    }

    void Drag(Point p, bool shift)
    {
        switch (tool)
        {
            case Tool.Arrow:
                if (shift)
                {
                    // snap to 45 degree steps
                    double a = Math.Round(Math.Atan2(p.Y - p0.Y, p.X - p0.X) / (Math.PI / 4)) * (Math.PI / 4), len = (p - p0).Length;
                    p = Inside(new Point(p0.X + len * Math.Cos(a), p0.Y + len * Math.Sin(a)));
                }
                cur!.Data = Arrow(p0, p, Widths[width]);
                break;
            case Tool.Line:
                if (shift) p = Snap45(p0, p);
                cur!.Data = new LineGeometry(p0, p);
                break;
            case Tool.Box:
            case Tool.Ellipse:
            case Tool.Pixel:
            case Tool.Blur:
            case Tool.Spot:
                if (shift)
                {
                    double w = p.X - p0.X, h = p.Y - p0.Y, a = Math.Max(Math.Abs(w), Math.Abs(h));
                    p = Inside(new Point(p0.X + (w < 0 ? -a : a), p0.Y + (h < 0 ? -a : a)));
                }
                if (tool == Tool.Ellipse) { cur!.Data = new EllipseGeometry(new Rect(p0, p)); break; }
                double rad = tool == Tool.Box ? 6 : tool == Tool.Spot ? 8 : 0;
                cur!.Data = new RectangleGeometry(new Rect(p0, p), rad, rad);
                if (tool == Tool.Spot) RefreshShade(new Rect(p0, p));   // show the spotlight while dragging
                break;
            case Tool.Pen:
            case Tool.Marker:
                if (snapped) { Reshape(p); break; }
                if (shift)
                {
                    // Shift = straight line, horizontal for the highlighter
                    line.Points.Clear();
                    if (tool == Tool.Marker) p = new Point(p.X, p0.Y);
                }
                line.Points.Add(p);
                if ((p - holdAt).Length > 2 || shift)
                {
                    // moved: restart the hold timer
                    holdAt = p;
                    holdTimer.Stop();
                    if (!shift) holdTimer.Start();
                }
                break;
        }
    }

    Point Snap45(Point a, Point p)
    {
        double ang = Math.Round(Math.Atan2(p.Y - a.Y, p.X - a.X) / (Math.PI / 4)) * (Math.PI / 4), len = (p - a).Length;
        return Inside(new Point(a.X + len * Math.Cos(ang), a.Y + len * Math.Sin(ang)));
    }

    List<Point> StrokePoints(Shapes.Path el)
    {
        var fig = ((PathGeometry)el.Data).Figures[0];
        var pts = new List<Point> { fig.StartPoint };
        pts.AddRange(line.Points);
        return pts;
    }
}
