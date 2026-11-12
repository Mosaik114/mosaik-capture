namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: placing markup (blur, spotlight, highlighter, pixelate, numbers, text) (fields and construction in ScreenshotEditor.cs).
internal sealed partial class ScreenshotEditor
{
    /// <summary>A stroke smaller than this in both directions was a click.</summary>
    const double MinStrokeSize = 3;

    internal void PointerUp()
    {
        if (mode == Mode.None) return;
        Mode finished = mode;
        mode = Mode.None;
        ReleaseMouseCapture();
        switch (finished)
        {
            case Mode.MoveItem:
                FinishMovingItem();
                break;
            case Mode.MoveCrop:
            case Mode.ResizeCrop:
                FinishCropChange();
                break;
            case Mode.Draw:
                FinishStroke();
                break;
        }
    }

    void FinishMovingItem()
    {
        var offset = itemOffset;
        double x0 = itemStartX, y0 = itemStartY, x1 = offset.X, y1 = offset.Y;
        if (x0 != x1 || y0 != y1) PushUndo(delegate { offset.X = x0; offset.Y = y0; }, delegate { offset.X = x1; offset.Y = y1; });
        RefreshSpotlightShade(null);
    }

    void FinishCropChange()
    {
        Rect before = cropAtPress, after = crop;
        if (before != after) PushUndo(delegate { crop = before; LayoutCrop(); }, delegate { crop = after; LayoutCrop(); });
    }

    // The drawn preview becomes markup: as drawn, smoothed, snapped to text, or turned into an effect area.
    void FinishStroke()
    {
        holdTimer.Stop();
        var drawn = stroke!;
        stroke = null;
        markup.Children.Remove(drawn);
        drawn.BeginAnimation(OpacityProperty, null);
        Rect bounds = drawn.Data == null ? Rect.Empty : drawn.Data.Bounds;
        if (bounds.IsEmpty || (bounds.Width < MinStrokeSize && bounds.Height < MinStrokeSize)) { RefreshSpotlightShade(null); return; }   // just a click
        if (!PlaceStroke(drawn, bounds)) return;
        snapped = false;
    }

    // False if nothing was placed and the stroke state must stay as it is (spotlight outside the crop).
    bool PlaceStroke(Shapes.Path drawn, Rect bounds)
    {
        if (tool == Tool.Pixel || tool == Tool.Blur)
        {
            var cover = tool == Tool.Pixel ? Pixelated(bounds) : Blurred(bounds);
            if (cover != null) AddMarkup(cover);
        }
        else if (tool == Tool.Spot)
        {
            bounds.Intersect(crop);
            if (bounds.IsEmpty) { RefreshSpotlightShade(null); return false; }
            AddMarkup(SpotlightFrame(bounds));
        }
        else if (tool == Tool.Marker && !snapped) AddMarkup(TextHighlight(StrokePoints(drawn), MarkerWidths[widthIndex]) ?? drawn);
        else if (tool == Tool.Pen && !snapped)
        {
            var smooth = ShapeRecognizer.Smooth(StrokePoints(drawn));
            if (smooth != null) drawn.Data = smooth;
            AddMarkup(drawn);
        }
        else AddMarkup(drawn);
        return true;
    }

    // An invisible frame for the spotlight hole; only its edge can be grabbed to move it.
    static Shapes.Path SpotlightFrame(Rect area)
    {
        var spot = new Shapes.Path
        {
            Data = new RectangleGeometry(new Rect(0, 0, area.Width, area.Height), Metrics.SpotlightRadius, Metrics.SpotlightRadius),
            Tag = SpotTag,
            Stroke = Brushes.Transparent,
            StrokeThickness = Metrics.SpotlightGrabWidth,
            RenderTransform = new TranslateTransform()
        };
        Canvas.SetLeft(spot, area.X);
        Canvas.SetTop(spot, area.Y);
        return spot;
    }

    // Blur: take a slightly larger crop so the edge does not fade into transparency, then clip to the area.
    FrameworkElement? Blurred(Rect r)
    {
        r.Intersect(crop);
        if (r.IsEmpty) return null;
        int x = (int)Math.Round(r.X * scale), y = (int)Math.Round(r.Y * scale);
        int w = Math.Min((int)Math.Round(r.Width * scale), capture.FullScreen.PixelWidth - x), h = Math.Min((int)Math.Round(r.Height * scale), capture.FullScreen.PixelHeight - y);
        if (w < 2 || h < 2) return null;
        const double radius = 14;
        int pad = (int)Math.Ceiling(radius * 2 * scale);
        int px = Math.Max(0, x - pad), py = Math.Max(0, y - pad);
        int pr = Math.Min(capture.FullScreen.PixelWidth, x + w + pad), pb = Math.Min(capture.FullScreen.PixelHeight, y + h + pad);
        var source = new CroppedBitmap(capture.FullScreen, new Int32Rect(px, py, pr - px, pb - py));
        source.Freeze();
        var img = new Image
        {
            Source = source,
            Width = (pr - px) / scale,
            Height = (pb - py) / scale,
            Stretch = Stretch.Fill,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality }
        };
        Canvas.SetLeft(img, (px - x) / scale);
        Canvas.SetTop(img, (py - y) / scale);
        var c = new Canvas { Width = w / scale, Height = h / scale, ClipToBounds = true, RenderTransform = new TranslateTransform(), Tag = CoverTag };
        c.Children.Add(img);
        Canvas.SetLeft(c, x / scale);
        Canvas.SetTop(c, y / scale);
        return c;
    }

    // Dimming for all spotlights: one area with holes, placed below all other markup.
    void RefreshSpotlightShade(Rect? extra)
    {
        Geometry? g = null;
        var holes = new List<Geometry>();
        foreach (UIElement c in markup.Children)
        {
            if (c is not Shapes.Path sp || !SpotTag.Equals(sp.Tag)) continue;
            var tt = (TranslateTransform)sp.RenderTransform;
            Rect r = sp.Data.Bounds;
            r.Offset(Canvas.GetLeft(sp) + tt.X, Canvas.GetTop(sp) + tt.Y);
            holes.Add(new RectangleGeometry(r, 8, 8));
        }
        if (extra.HasValue) holes.Add(new RectangleGeometry(extra.Value, 8, 8));
        if (holes.Count == 0)
        {
            if (spotlightShade != null) markup.Children.Remove(spotlightShade);
            return;
        }
        g = new RectangleGeometry(new Rect(0, 0, root.Width, root.Height));
        foreach (var h in holes) g = Geometry.Combine(g, h, GeometryCombineMode.Exclude, null);
        if (spotlightShade == null) spotlightShade = new Shapes.Path { Fill = Palette.Frozen(0x99, 0x14, 0x14, 0x13), IsHitTestVisible = false };
        spotlightShade.Data = g;
        markup.Children.Remove(spotlightShade);
        int under = 0;
        foreach (UIElement c in markup.Children) if (CoverTag.Equals(((FrameworkElement)c).Tag)) under++;
        markup.Children.Insert(under, spotlightShade);
    }

    // Highlighter over text: snap to the line height and word boundaries; null if no text is recognizable there.
    FrameworkElement? TextHighlight(List<Point> pts, double thick)
    {
        Rect b = ShapeRecognizer.Bounds(pts);
        if (b.Width < 12 || b.Height > b.Width * 0.35) return null;   // only nearly horizontal strokes
        Rect dev = TextLineFinder.Find(capture.FullScreen, b.Left * scale, b.Right * scale, (b.Top + b.Bottom) / 2 * scale, thick / 2 * scale);
        if (dev.IsEmpty) return null;
        double m = 2;
        Rect r = new Rect(dev.X / scale - m, dev.Y / scale - m, dev.Width / scale + 2 * m, dev.Height / scale + 2 * m);
        r.Intersect(crop);
        if (r.IsEmpty) return null;
        var hl = new Shapes.Path { Data = new RectangleGeometry(new Rect(0, 0, r.Width, r.Height), 3, 3), Fill = MarkerBrush(), RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(hl, r.X);
        Canvas.SetTop(hl, r.Y);
        return hl;
    }


    // Coarsely pixelates an area: scale down, then scale up again with hard edges.
    FrameworkElement? Pixelated(Rect r)
    {
        r.Intersect(crop);
        if (r.IsEmpty) return null;
        int x = (int)Math.Round(r.X * scale), y = (int)Math.Round(r.Y * scale);
        int w = Math.Min((int)Math.Round(r.Width * scale), capture.FullScreen.PixelWidth - x), h = Math.Min((int)Math.Round(r.Height * scale), capture.FullScreen.PixelHeight - y);
        if (w < 2 || h < 2) return null;
        double block = Math.Max(6, Math.Round(11 * scale));
        var source = new CroppedBitmap(capture.FullScreen, new Int32Rect(x, y, w, h));
        var small = new TransformedBitmap(source, new ScaleTransform(Math.Max(1, Math.Round(w / block)) / w, Math.Max(1, Math.Round(h / block)) / h));
        small.Freeze();
        var img = new Image { Source = small, Width = w / scale, Height = h / scale, Stretch = Stretch.Fill, RenderTransform = new TranslateTransform(), Tag = CoverTag };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, x / scale);
        Canvas.SetTop(img, y / scale);
        return img;
    }

    int NextNumber()
    {
        int n = 1;
        foreach (UIElement c in markup.Children)
        {
            if (c is FrameworkElement fe && NumberTag.Equals(fe.Tag)) n++;
        }
        return n;
    }

    // Digits as geometry whose visible outline is centered exactly on point c
    // (a TextBlock line reserves room for ascenders and descenders and is therefore never quite centered).
    static Geometry Digits(string text, double em, Point c)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Palette.DigitFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), em, Brushes.White, 1.0);
        var g = ft.BuildGeometry(new Point(0, 0));
        Rect b = g.Bounds;
        g.Transform = new TranslateTransform(c.X - b.X - b.Width / 2, c.Y - b.Y - b.Height / 2);
        g.Freeze();
        return g;
    }

    // Number badge: a circle, a pill from two digits on; its center is at (h/2, h/2) measured from the left edge.
    static FrameworkElement NumberBadge(int n, Color col, double h)
    {
        string text = n.ToString(CultureInfo.CurrentCulture);
        bool bright = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255 > 0.7;
        double em = Math.Round(h * 0.56 * 2) / 2;
        Geometry probe = Digits(text, em, new Point(0, 0));
        double w = text.Length < 2 ? h : Math.Round(Math.Max(h, probe.Bounds.Width + h * 0.62));
        var g = new Grid
        {
            Width = w,
            Height = h,
            Tag = NumberTag,
            RenderTransform = new TranslateTransform(),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 7, ShadowDepth = 1.5, Direction = 270, Opacity = 0.38 }
        };
        g.Children.Add(new Border
        {
            Background = new SolidColorBrush(col),
            CornerRadius = new CornerRadius(h / 2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)),
            BorderThickness = new Thickness(bright ? 1 : 0)   // light colors need an outline on white
        });
        g.Children.Add(new Shapes.Path
        {
            Data = Digits(text, em, new Point(w / 2, h / 2)),
            Fill = bright ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x24)) : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        return g;
    }

    void PlaceNumber(Point p)
    {
        double h = BadgeSizes[widthIndex];
        var g = NumberBadge(NextNumber(), InkColors[colorIndex], h);
        Canvas.SetLeft(g, Math.Round(p.X - g.Width / 2));
        Canvas.SetTop(g, Math.Round(p.Y - h / 2));
        AddMarkup(g);
    }

    // Preview of the next number at the pointer; true while it is visible.
    bool UpdateNumberPreview()
    {
        bool show = tool == Tool.Number && mode == Mode.None && textBox == null && crop.Contains(pointer) && !toolbar.IsMouseOver;
        if (!show)
        {
            numberPreview?.Visibility = Visibility.Hidden;
            return false;
        }
        int n = NextNumber();
        string key = n + "|" + colorIndex + "|" + widthIndex;
        if (key != numberPreviewKey)
        {
            if (numberPreview != null) root.Children.Remove(numberPreview);
            numberPreview = NumberBadge(n, InkColors[colorIndex], BadgeSizes[widthIndex]);
            numberPreview.Opacity = 0.7;
            numberPreview.IsHitTestVisible = false;
            root.Children.Insert(root.Children.IndexOf(markup) + 1, numberPreview);
            numberPreviewKey = key;
        }
        Canvas.SetLeft(numberPreview, Math.Round(pointer.X - numberPreview!.Width / 2));
        Canvas.SetTop(numberPreview, Math.Round(pointer.Y - numberPreview.Height / 2));
        numberPreview.Visibility = Visibility.Visible;
        return true;
    }

    static System.Windows.Media.Effects.Effect TextShadow()
    {
        return new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black };
    }

    static bool Bright(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.7; }

    static System.Windows.Media.Effects.Effect PlateShadow()
    {
        return new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Direction = 270, Opacity = 0.35 };
    }

    // Plate around the text: corner radius and padding grow with the font size.
    static Thickness PlatePad(double fs) { return new Thickness(Math.Round(fs * 0.45), Math.Round(fs * 0.16), Math.Round(fs * 0.45), Math.Round(fs * 0.2)); }

    void StartTextInput(Point p, bool plate)
    {
        double fs = TextSizes[widthIndex];
        var tb = new TextBox
        {
            FontFamily = Palette.UiFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = fs,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 12,
            FocusVisualStyle = null
        };
        textBox = tb;
        if (plate)
        {
            var host = new Border { Child = tb, Effect = PlateShadow(), Tag = PlateTag };
            textHost = host;
            StyleTextBox();
            Canvas.SetLeft(host, p.X - host.Padding.Left);
            Canvas.SetTop(host, p.Y - fs * 0.7 - host.Padding.Top);
        }
        else
        {
            tb.RenderTransform = new TranslateTransform();
            tb.Effect = TextShadow();
            textHost = tb;
            StyleTextBox();
            Canvas.SetLeft(tb, p.X - 3);
            Canvas.SetTop(tb, p.Y - fs * 0.7);
        }
        markup.Children.Add(textHost);
        tb.Focus();
        Keyboard.Focus(tb);
    }

    // Applies color and size to the open text box.
    void StyleTextBox()
    {
        var tb = textBox!;
        double fs = TextSizes[widthIndex];
        tb.FontSize = fs;
        if (textHost is Border host)
        {
            Color c = InkColors[colorIndex];
            host.Background = new SolidColorBrush(c);
            host.CornerRadius = new CornerRadius(Math.Round(fs * 0.3));
            host.Padding = PlatePad(fs);
            tb.Foreground = tb.CaretBrush = Bright(c) ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x24)) : Brushes.White;
        }
        else tb.Foreground = tb.CaretBrush = new SolidColorBrush(InkColors[colorIndex]);
    }

    // Finishes text input: the text box becomes fixed text at the same position.
    internal void CommitText()
    {
        var tb = textBox;
        if (tb == null) return;
        var hostEl = textHost;
        textBox = null;
        textHost = null;
        Rect first = tb.GetRectFromCharacterIndex(0);
        int idx = markup.Children.IndexOf(hostEl);
        markup.Children.Remove(hostEl);
        Keyboard.Focus(this);
        if (tb.Text.Trim().Length == 0 || idx < 0) return;
        double fx = first.IsEmpty ? 2 : first.X, fy = first.IsEmpty ? 0 : first.Y;
        var t = new TextBlock { Text = tb.Text, FontFamily = tb.FontFamily, FontWeight = tb.FontWeight, FontSize = tb.FontSize, Foreground = tb.Foreground };
        FrameworkElement el;
        if (hostEl is Border plate)
        {
            // same spacing left and right so the plate looks balanced
            plate.Child = null;
            t.Margin = new Thickness(fx, fy, fx, 0);
            plate.Child = t;
            plate.RenderTransform = new TranslateTransform();
            el = plate;
        }
        else
        {
            t.Effect = TextShadow();
            t.RenderTransform = new TranslateTransform();
            Canvas.SetLeft(t, Canvas.GetLeft(tb) + fx);
            Canvas.SetTop(t, Canvas.GetTop(tb) + fy);
            el = t;
        }
        markup.Children.Insert(idx, el);
        PushUndo(delegate { markup.Children.Remove(el); }, delegate { markup.Children.Add(el); });
    }

    // Topmost markup under the pointer (with some tolerance so thin lines can be hit).
    FrameworkElement? ItemAt(Point p)
    {
        FrameworkElement? found = null;
        VisualTreeHelper.HitTest(markup, null, r =>
        {
            DependencyObject o = r.VisualHit;
            while (o != null && VisualTreeHelper.GetParent(o) != markup) o = VisualTreeHelper.GetParent(o);
            found = o as FrameworkElement;
            return found != null ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(new EllipseGeometry(p, 5, 5)));
        return found != null && found.RenderTransform is TranslateTransform ? found : null;
    }
}
