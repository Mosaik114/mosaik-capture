namespace MosaikCapture.Engine.Screenshot;

// Editor: the frozen image stays dimmed, the crop is bright,
// has handles for re-cropping and the toolbar below it.
// Related tools share a button; a second click on the active button shows the choices.
// Keys: V move, A arrow, R rectangle, E ellipse, L line, P pen, M highlighter, S spotlight,
// T text, B text with background, N number, X pixelate, W blur, 1-6 color, +/- or mouse wheel size,
// Ctrl+Z / Ctrl+Y, Del, Ctrl+C copy, Ctrl+Shift+C copy text, Ctrl+S save as, Enter done, Esc cancel.
// Pen: hold still at the end = clean shape; highlighter: snaps onto the text line it crosses.
partial class ShotEditor : Window
{
    enum Tool { Move, Arrow, Box, Ellipse, Line, Pen, Marker, Spot, Text, Label, Number, Pixel, Blur }
    static readonly string[] ToolNames =
    {
        "Verschieben", "Pfeil", "Rechteck", "Kreis", "Linie", "Stift", "Textmarker", "Scheinwerfer",
        "Text", "Text mit Hintergrund", "Nummer", "Verpixeln", "Weichzeichnen"
    };
    static readonly Key[] ToolKeys = { Key.V, Key.A, Key.R, Key.E, Key.L, Key.P, Key.M, Key.S, Key.T, Key.B, Key.N, Key.X, Key.W };
    enum Mode { None, Draw, Item, Frame, Resize }
    sealed class Step
    {
        public readonly Action Undo, Redo;
        public Step(Action undo, Action redo) { Undo = undo; Redo = redo; }
    }
    sealed class ToolGroup
    {
        public string? Title;
        public Tool[] Tools = null!;
        public Tool Current;
        public Border Btn = null!;   // created right after the group
        public Grid Host = null!;
        public SolidColorBrush Ink = null!;
    }

    static readonly Color[] Inks =
    {
        Color.FromRgb(0xD9, 0x77, 0x57), Color.FromRgb(0xF0, 0x80, 0x6A), Color.FromRgb(0xF2, 0xC9, 0x4C),
        Color.FromRgb(0x6F, 0xBF, 0x8E), Color.FromRgb(0x6F, 0xA8, 0xDC), Color.FromRgb(0xFA, 0xF9, 0xF5)
    };
    static readonly double[] Widths = { 2, 3, 5, 8, 12 }, Markers = { 10, 14, 22, 32, 44 }, TextSizes = { 16, 20, 26, 34, 44 }, Dots = { 20, 24, 30, 38, 48 };
    static readonly string[] WidthNames = { "Sehr fein", "Fein", "Mittel", "Dick", "Sehr dick" };
    static readonly FontFamily DigitFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
    static readonly double[] HX = { 0, 0.5, 1, 1, 1, 0.5, 0, 0 }, HY = { 0, 0, 0, 0.5, 1, 1, 1, 0.5 };
    static readonly Cursor[] HCursor =
    {
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE
    };
    static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly Action<ShotData, BitmapSource> done, pinned;
    readonly Action<ShotData> cancelled;
    readonly IntPtr hwnd;
    readonly Canvas root, ink;
    readonly Image back;
    readonly Shapes.Path dim;
    readonly Shapes.Rectangle frame;
    readonly Border[] handles = new Border[8];
    readonly Border sizeChip, bar, tip, note;
    readonly TextBlock sizeText, tipText, noteText;
    readonly StackPanel row;
    readonly List<ToolGroup> groups = new List<ToolGroup>();
    readonly Border menu;              // tool choices of a group button
    readonly StackPanel menuList;
    ToolGroup? menuFor;
    readonly DispatcherTimer holdTimer;   // pen/highlighter held still
    Point holdAt;
    bool snapped;                         // the stroke has already snapped to a clean shape
    Geometry? snapShape;
    Point snapCenter, snapStart;
    double snapReach;
    bool snapOpen, snapArrow;
    Shapes.Path? shade;     // dimming for the spotlight
    FrameworkElement? typingHost;          // the text box itself or the plate around it
    readonly List<Shapes.Ellipse> colorRings = new List<Shapes.Ellipse>();
    readonly List<Border> widthBtn = new List<Border>();
    readonly List<Step> undo = new List<Step>(), redo = new List<Step>();
    readonly DispatcherTimer noteTimer;

    ShotData data = null!;   // set by Open(), cleared by Leave()
    double s = 1;          // device pixels per WPF unit
    Rect sel;              // crop in WPF units
    Tool tool = Tool.Arrow;
    int color, width = 2;
    FrameworkElement? ghost;   // semi-transparent preview of the next number at the pointer
    string? ghostKey;
    Point hover;
    Mode mode;
    Point p0;
    Rect sel0;
    int handle;
    Shapes.Path? cur;
    PolyLineSegment line = null!;   // points of the current pen or highlighter stroke
    FrameworkElement? picked;
    TranslateTransform itemMove = null!;   // transform of the item being moved
    double itemX0, itemY0;
    TextBox? typing;
    bool escArmed;

    public ShotEditor(Action<ShotData, BitmapSource> onDone, Action<ShotData> onCancel, Action<ShotData, BitmapSource> onPin)
    {
        done = onDone;
        cancelled = onCancel;
        pinned = onPin;
        Title = "Mosaiks-Screenshot bearbeiten";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Palette.Apply(Resources);

        root = new Canvas { ClipToBounds = true };
        back = new Image { Stretch = Stretch.Fill };
        root.Children.Add(back);
        dim = new Shapes.Path { Fill = ShotOverlay.Frozen(0xA6, 0x14, 0x14, 0x13), IsHitTestVisible = false };
        root.Children.Add(dim);
        ink = new Canvas();
        root.Children.Add(ink);
        frame = new Shapes.Rectangle { StrokeThickness = 2, IsHitTestVisible = false };
        frame.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
        root.Children.Add(frame);
        for (int i = 0; i < 8; i++)
        {
            var dot = new Shapes.Ellipse { Width = 11, Height = 11, StrokeThickness = 2 };
            dot.SetResourceReference(Shapes.Shape.FillProperty, "Text");
            dot.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
            var h = new Border { Width = 19, Height = 19, Background = Brushes.Transparent, Child = dot, Cursor = HCursor[i] };   // larger hit area
            int k = i;
            h.MouseLeftButtonDown += (o, e) =>
            {
                Commit();
                mode = Mode.Resize;
                handle = k;
                sel0 = sel;
                CaptureMouse();
                e.Handled = true;
            };
            handles[i] = h;
            root.Children.Add(h);
        }
        sizeText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = UiFont };
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sizeChip = ShotOverlay.Chip(sizeText);
        sizeChip.IsHitTestVisible = false;
        root.Children.Add(sizeChip);

        // Toolbar
        row = new StackPanel { Orientation = Orientation.Horizontal };
        AddGroup(null, Tool.Move);
        Sep();
        AddGroup(null, Tool.Arrow);
        AddGroup("Formen", Tool.Box, Tool.Ellipse, Tool.Line);
        AddGroup(null, Tool.Pen);
        AddGroup("Hervorheben", Tool.Marker, Tool.Spot);
        AddGroup("Text", Tool.Text, Tool.Label);
        AddGroup(null, Tool.Number);
        AddGroup("Verdecken", Tool.Pixel, Tool.Blur);
        Sep();
        for (int i = 0; i < Inks.Length; i++)
        {
            var g = new Grid { Width = 26, Height = 36, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var ring = new Shapes.Ellipse { Width = 24, Height = 24, StrokeThickness = 1.5, Visibility = Visibility.Hidden };
            ring.SetResourceReference(Shapes.Shape.StrokeProperty, "Text");
            g.Children.Add(ring);
            g.Children.Add(new Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Inks[i]) });
            colorRings.Add(ring);
            int k = i;
            g.MouseLeftButtonUp += delegate { SetColor(k); };
            g.MouseEnter += delegate { ShowTip(g, "Farbe   " + (k + 1)); };
            g.MouseLeave += delegate { tip!.Visibility = Visibility.Hidden; };
            row.Children.Add(g);
        }
        var widths = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        double[] dots = { 4, 6, 8, 10, 12 };   // even sizes so they sit exactly centered in the 24 px box
        for (int i = 0; i < dots.Length; i++)
        {
            var e = new Shapes.Ellipse { Width = dots[i], Height = dots[i] };
            e.SetResourceReference(Shapes.Shape.FillProperty, "Text");
            var b = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Child = e, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            int k = i;
            b.MouseLeftButtonUp += delegate { SetWidth(k); };
            b.MouseEnter += delegate { Paint(b, true); ShowTip(b, WidthNames[k] + "   + / −"); };
            b.MouseLeave += delegate { Paint(b, false); tip!.Visibility = Visibility.Hidden; };
            widthBtn.Add(b);
            widths.Children.Add(b);
        }
        row.Children.Add(widths);
        Sep();
        row.Children.Add(Button("\uE7A7", "Rückgängig   Strg+Z", Undo, false));
        row.Children.Add(Button("\uE7A6", "Wiederholen   Strg+Y", Redo, false));
        Sep();
        row.Children.Add(Button("\uE74E", "Speichern unter   Strg+S", SaveAs, false));
        row.Children.Add(Button("\uE8C8", "Kopieren   Strg+C", CopyNow, false));
        row.Children.Add(Button("\uE8AC", "Text kopieren   Strg+Umschalt+C", CopyText, false));
        row.Children.Add(Button("\uE711", "Abbrechen   Esc", Cancel, false));
        row.Children.Add(Button("\uE73E", "Fertig   Enter", delegate { Finish(false); }, true));
        row.Children.Insert(row.Children.Count - 2, Button("\uE718", "Anpinnen   F3", delegate { Finish(true); }, false));
        bar = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
            Child = row,
            Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.5 }
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Bg");
        bar.SetResourceReference(Border.BorderBrushProperty, "Line");
        bar.MouseLeftButtonDown += (o, e) => e.Handled = true;   // clicks on the toolbar do not draw
        root.Children.Add(bar);

        menuList = new StackPanel();
        menu = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            Child = menuList,
            Cursor = Cursors.Arrow,
            MinWidth = 196,
            Visibility = Visibility.Hidden,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.4 }
        };
        menu.SetResourceReference(Border.BackgroundProperty, "Bg");
        menu.SetResourceReference(Border.BorderBrushProperty, "Line");
        menu.MouseLeftButtonDown += (o, e) => e.Handled = true;
        root.Children.Add(menu);

        holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        holdTimer.Tick += delegate { holdTimer.Stop(); Held(); };

        tipText = new TextBlock { FontSize = 12, FontFamily = UiFont };
        tipText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        tip = ShotOverlay.Chip(tipText);
        tip.IsHitTestVisible = false;
        tip.Visibility = Visibility.Hidden;
        tip.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.4 };
        root.Children.Add(tip);
        noteText = new TextBlock { FontSize = 12.5, FontFamily = UiFont, FontWeight = FontWeights.SemiBold };
        noteText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        note = ShotOverlay.Chip(noteText);
        note.CornerRadius = new CornerRadius(10);
        note.Padding = new Thickness(14, 8, 14, 8);
        note.IsHitTestVisible = false;
        note.Visibility = Visibility.Hidden;
        note.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.4 };
        root.Children.Add(note);
        Content = root;

        noteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        noteTimer.Tick += delegate { noteTimer.Stop(); note.Visibility = Visibility.Hidden; escArmed = false; };

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        _ = SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80);   // WS_EX_TOOLWINDOW

        MouseLeftButtonDown += (o, e) => Down(e.GetPosition(root));
        MouseMove += (o, e) => Moved(e.GetPosition(root));
        MouseLeftButtonUp += (o, e) => Up();
        MouseLeave += delegate { hover = new Point(-1e6, -1e6); UpdateGhost(); };
        MouseWheel += (o, e) => { if (typing == null && mode == Mode.None) StepWidth(e.Delta > 0 ? 1 : -1); };
        PreviewKeyDown += (o, e) => OnKey(e);
    }

    // ---------- Toolbar ----------

    static TextBlock Glyph(string g, Brush? b)
    {
        return new TextBlock { Text = g, FontSize = 16, FontFamily = IconFont, Foreground = b, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    void Sep()
    {
        var r = new Shapes.Rectangle { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Shapes.Shape.FillProperty, "Line");
        row.Children.Add(r);
    }

    Border Button(string glyph, string label, Action run, bool primary)
    {
        var t = Glyph(glyph, primary ? Brushes.White : null);
        if (!primary) t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        return Wrap(t, label, run, primary);
    }

    Border Wrap(UIElement content, string label, Action run, bool primary)
    {
        var b = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(1, 0, 1, 0),
            Child = content,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = primary ? "primary" : null
        };
        if (primary) b.SetResourceReference(Border.BackgroundProperty, "Accent");
        b.MouseEnter += delegate { Paint(b, true); ShowTip(b, label); };
        b.MouseLeave += delegate { Paint(b, false); tip.Visibility = Visibility.Hidden; };
        b.MouseLeftButtonUp += delegate { run(); };
        return b;
    }

    // Button for one tool or a group of related tools; a group shows the tool picked last.
    void AddGroup(string? title, params Tool[] tools)
    {
        var grp = new ToolGroup { Title = title, Tools = tools, Current = tools[0], Ink = new SolidColorBrush(Colors.White), Host = new Grid() };
        UIElement content = grp.Host;
        if (tools.Length > 1)
        {
            // small triangle bottom right: there is more
            var more = new Grid { Width = 36, Height = 36 };
            more.Children.Add(grp.Host);
            var tri = new Shapes.Path
            {
                Data = Geometry.Parse("M 30,26 L 30,30 L 26,30 Z"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            tri.SetResourceReference(Shapes.Shape.FillProperty, "Muted");
            more.Children.Add(tri);
            content = more;
        }
        grp.Btn = Wrap(content, "", delegate
        {
            if (Array.IndexOf(grp.Tools, tool) >= 0 && grp.Tools.Length > 1)
            {
                if (menuFor == grp) CloseMenu(); else OpenMenu(grp);
            }
            else SetTool(grp.Current);
        }, false);
        grp.Btn.MouseEnter += delegate { if (menuFor == null) ShowTip(grp.Btn, Label(grp)); else tip.Visibility = Visibility.Hidden; };
        ShowIcon(grp);
        groups.Add(grp);
        row.Children.Add(grp.Btn);
    }

    string Label(ToolGroup g)
    {
        string t = ToolNames[(int)g.Current] + "   " + ToolKeys[(int)g.Current];
        if (g.Tools.Length > 1) t += Array.IndexOf(g.Tools, tool) >= 0 ? "   ·   Klick: mehr" : "";
        return t;
    }

    static void ShowIcon(ToolGroup g)
    {
        g.Host.Children.Clear();
        g.Host.Children.Add(ToolIcon(g.Current, g.Ink));
    }

    // Menu of a tool group: icon, name and key, right next to the button.
    void OpenMenu(ToolGroup g)
    {
        tip.Visibility = Visibility.Hidden;
        menuFor = g;
        menuList.Children.Clear();
        foreach (Tool t in g.Tools)
        {
            Tool k = t;
            bool on = t == tool;
            var fg = new SolidColorBrush(Res(on ? "Accent" : "Text"));
            var icon = new Grid { Width = 26, HorizontalAlignment = HorizontalAlignment.Left };
            icon.Children.Add(ToolIcon(t, fg));
            var key = new TextBlock { Text = ToolKeys[(int)t].ToString(), FontSize = 11.5, FontFamily = UiFont, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var name = new TextBlock { Text = ToolNames[(int)t], FontSize = 12.5, FontFamily = UiFont, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var d = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(key, Dock.Right);
            d.Children.Add(icon);
            d.Children.Add(key);
            d.Children.Add(name);
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 7, 12, 7), Child = d, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = on ? "on" : null };
            Paint(b, false);
            b.MouseEnter += delegate { Paint(b, true); };
            b.MouseLeave += delegate { Paint(b, false); };
            b.MouseLeftButtonUp += delegate { SetTool(k); };
            menuList.Children.Add(b);
        }
        menu.Visibility = Visibility.Visible;
        menu.InvalidateMeasure();   // otherwise the height from the last time sticks
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point pt = g.Btn.TranslatePoint(new Point(0, 0), root);
        double top = Canvas.GetTop(bar), mh = menu.DesiredSize.Height;
        Rect m = Mon();
        Canvas.SetLeft(menu, Math.Max(m.Left + 4, Math.Min(m.Right - 4 - menu.DesiredSize.Width, pt.X - 6)));
        Canvas.SetTop(menu, top + bar.ActualHeight + 6 + mh < m.Bottom - 4 ? top + bar.ActualHeight + 6 : top - 6 - mh);
    }

    void CloseMenu()
    {
        menuFor = null;
        menu.Visibility = Visibility.Hidden;
    }

    // Icons: font glyphs where a suitable one exists, otherwise drawn in the same stroke style.
    static UIElement ToolIcon(Tool t, SolidColorBrush fg)
    {
        switch (t)
        {
            case Tool.Move: return Glyph("\uE7C9", fg);
            case Tool.Arrow: return Glyph("\uE72A", fg);
            case Tool.Box: return Glyph("\uE739", fg);
            case Tool.Ellipse: return Glyph("\uEA3A", fg);
            case Tool.Pen: return Glyph("\uEE56", fg);
            case Tool.Marker: return Glyph("\uE7E6", fg);
            case Tool.Text: return Glyph("\uE8D2", fg);
        }
        var c = new Canvas { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (t == Tool.Line)
            c.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 2.5,13.5 L 13.5,2.5"), Stroke = fg, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        else if (t == Tool.Spot)
        {
            // dimmed area with a bright hole
            var hole = new EllipseGeometry(new Point(8, 8), 4.5, 4.5);
            c.Children.Add(new Shapes.Path { Data = Geometry.Combine(new RectangleGeometry(new Rect(1, 1, 14, 14), 2.5, 2.5), hole, GeometryCombineMode.Exclude, null), Fill = fg, Opacity = 0.45 });
            c.Children.Add(new Shapes.Path { Data = new EllipseGeometry(new Point(8, 8), 4.5, 4.5), Stroke = fg, StrokeThickness = 1.25 });
        }
        else if (t == Tool.Label)
        {
            // filled plate with the A punched out
            var plate = new RectangleGeometry(new Rect(0, 1.5, 16, 13), 3, 3);
            c.Children.Add(new Shapes.Path { Data = Geometry.Combine(plate, Digits("A", 11, new Point(8, 8)), GeometryCombineMode.Exclude, null), Fill = fg });
        }
        else if (t == Tool.Blur)
        {
            // drop, as is common for blur tools
            c.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 8,1.5 C 8,1.5 3,7.4 3,10.3 A 5,5 0 0 0 13,10.3 C 13,7.4 8,1.5 8,1.5 Z"), Stroke = fg, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
            c.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 5.6,10.6 A 2.6,2.6 0 0 0 7.6,12.7"), Stroke = fg, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.6 });
        }
        else if (t == Tool.Number)
        {
            // circle and digit as geometry, the digit centered by its visible outline
            var g = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new Shapes.Ellipse { Stroke = fg, StrokeThickness = 1.5 });
            g.Children.Add(new Shapes.Path
            {
                Data = Digits("1", 11.5, new Point(9, 9)),
                Fill = fg,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            });
            return g;
        }
        else
        {
            // 3x3 squares on whole pixels: 4 wide, 1 gap
            var g = new Canvas { Width = 14, Height = 14, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 9; i++)
            {
                var r = new Shapes.Rectangle { Width = 4, Height = 4, Fill = fg, Opacity = i % 2 == 0 ? 1 : 0.4 };
                Canvas.SetLeft(r, i % 3 * 5);
                Canvas.SetTop(r, i / 3 * 5);
                g.Children.Add(r);
            }
            return g;
        }
        return c;
    }

    // Button background: active = Sel, mouse over = Hover, otherwise transparent.
    static void Paint(Border b, bool hover)
    {
        string? st = b.Tag as string;
        if (st == "primary") b.Opacity = hover ? 0.88 : 1;
        else if (st == "on") b.SetResourceReference(Border.BackgroundProperty, "Sel");
        else if (hover) b.SetResourceReference(Border.BackgroundProperty, "Hover");
        else b.Background = Brushes.Transparent;
    }

    Color Res(string key) { return ((SolidColorBrush)Resources[key]).Color; }

    void SetTool(Tool t)
    {
        Commit();
        CloseMenu();
        tool = t;
        foreach (var g in groups)
        {
            bool on = Array.IndexOf(g.Tools, t) >= 0;
            if (on && g.Current != t) { g.Current = t; ShowIcon(g); }
            g.Btn.Tag = on ? "on" : null;
            Paint(g.Btn, g.Btn.IsMouseOver);
            g.Ink.Color = Res(on ? "Accent" : "Text");
            if (g.Btn.IsMouseOver) ShowTip(g.Btn, Label(g));
        }
        if (t != Tool.Move) picked = null;
        UpdateGhost();
    }

    void SetColor(int i)
    {
        color = i;
        for (int k = 0; k < colorRings.Count; k++) colorRings[k].Visibility = k == i ? Visibility.Visible : Visibility.Hidden;
        if (typing != null) StyleTyping();
        UpdateGhost();
    }

    void SetWidth(int i)
    {
        width = i;
        for (int k = 0; k < widthBtn.Count; k++)
        {
            widthBtn[k].Tag = k == i ? "on" : null;
            Paint(widthBtn[k], widthBtn[k].IsMouseOver);
        }
        if (typing != null) StyleTyping();
        UpdateGhost();
    }

    // One size step via the mouse wheel or +/-.
    void StepWidth(int dir)
    {
        int i = Math.Max(0, Math.Min(Widths.Length - 1, width + dir));
        if (i == width) return;
        SetWidth(i);
        Note("Größe: " + WidthNames[i]);
    }

    void ShowTip(FrameworkElement b, string text)
    {
        tipText.Text = text;
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point pt = b.TranslatePoint(new Point(0, 0), root);
        double top = Canvas.GetTop(bar), bh = bar.ActualHeight, th = tip.DesiredSize.Height;
        Rect m = Mon();
        Canvas.SetLeft(tip, Math.Max(m.Left + 4, Math.Min(m.Right - 4 - tip.DesiredSize.Width, pt.X + b.ActualWidth / 2 - tip.DesiredSize.Width / 2)));
        Canvas.SetTop(tip, top + bh + 8 + th < m.Bottom - 4 ? top + bh + 8 : top - 8 - th);
        tip.Visibility = Visibility.Visible;
    }

    void Note(string text)
    {
        noteText.Text = text;
        note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double top = Canvas.GetTop(bar), nh = note.DesiredSize.Height;
        Rect m = Mon();
        Canvas.SetLeft(note, Canvas.GetLeft(bar) + (bar.ActualWidth - note.DesiredSize.Width) / 2);
        Canvas.SetTop(note, top - 12 - nh > m.Top + 4 ? top - 12 - nh : top + bar.ActualHeight + 12);
        note.Visibility = Visibility.Visible;
        noteTimer.Stop();
        noteTimer.Start();
    }
}
