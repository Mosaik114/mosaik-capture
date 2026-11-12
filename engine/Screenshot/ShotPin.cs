namespace MosaikCapture.Engine.Screenshot;

// Pinned screenshot: floats above everything, exactly where it was captured.
// Drag = move, mouse wheel = zoom, Ctrl+wheel = opacity, double click / Esc = close,
// right click = menu (copy, save as, original size, close).
class ShotPin : Window
{
    const double Pad = 14;   // room for the shadow
    static readonly List<ShotPin> all = new List<ShotPin>();
    static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly BitmapSource img;
    readonly string name;
    readonly IntPtr hwnd;
    readonly Image view;
    readonly Border frame, close, chip;
    readonly TextBlock chipText;
    readonly System.Windows.Controls.Primitives.Popup menu;
    readonly DispatcherTimer chipTimer;
    double sc = 1, zoom = 1;

    public static void Open(BitmapSource img, int x, int y, string name)
    {
        var p = new ShotPin(img, name);
        all.Add(p);
        p.Place(x, y);
    }

    public static void CloseAll()
    {
        foreach (var p in all.ToArray()) p.Close();
    }

    ShotPin(BitmapSource image, string file)
    {
        img = image;
        name = file;
        Title = "Mosaiks-Screenshot angepinnt";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Palette.Apply(Resources);

        view = new Image { Source = img, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
        var g = new Grid();
        g.Children.Add(view);
        // the border lies on top of the image instead of around it so the image sits pixel-exactly where it was
        var line = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        line.SetResourceReference(Border.BorderBrushProperty, "Line");
        g.Children.Add(line);

        var x = new TextBlock { Text = "\uE711", FontSize = 10, FontFamily = IconFont, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        close = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(6),
            Child = x,
            Cursor = Cursors.Hand,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x26, 0x26, 0x24)),
            ToolTip = "Schließen   Esc"
        };
        close.MouseEnter += delegate { close.SetResourceReference(Border.BackgroundProperty, "Accent"); };
        close.MouseLeave += delegate { close.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x26, 0x26, 0x24)); };
        close.MouseLeftButtonDown += (o, e) => e.Handled = true;
        close.MouseLeftButtonUp += (o, e) => { e.Handled = true; Close(); };
        g.Children.Add(close);

        chipText = new TextBlock { FontSize = 11.5, FontFamily = UiFont, FontWeight = FontWeights.SemiBold };
        chipText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        chip = ShotOverlay.Chip(chipText);
        chip.Margin = new Thickness(8);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.VerticalAlignment = VerticalAlignment.Bottom;
        chip.IsHitTestVisible = false;
        chip.Visibility = Visibility.Collapsed;
        g.Children.Add(chip);
        chipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        chipTimer.Tick += delegate { chipTimer.Stop(); chip.Visibility = Visibility.Collapsed; };

        frame = new Border
        {
            Margin = new Thickness(Pad),
            Child = g,
            Cursor = Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black }
        };
        Content = frame;

        menu = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade
        };
        var items = new StackPanel();
        items.Children.Add(Item("\uE8C8", "Kopieren", "Strg+C", Copy));
        items.Children.Add(Item("\uE74E", "Speichern unter …", "Strg+S", SaveAs));
        items.Children.Add(Item("\uE71F", "Originalgröße", "0", delegate { Zoom(1, false); }));
        items.Children.Add(Sep());
        items.Children.Add(Item("\uE711", "Schließen", "Esc", Close));
        items.Children.Add(Item("\uE8BB", "Alle Pins schließen", "", CloseAll));
        var box = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            Child = items,
            Margin = new Thickness(0, 0, 12, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.4 }
        };
        box.SetResourceReference(Border.BackgroundProperty, "Bg");
        box.SetResourceReference(Border.BorderBrushProperty, "Line");
        menu.Child = box;

        frame.MouseEnter += delegate { Fade(close, 1); };
        frame.MouseLeave += delegate { if (!menu.IsOpen) Fade(close, 0); };
        frame.MouseLeftButtonDown += (o, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            Activate();
            try { DragMove(); } catch (InvalidOperationException) { }   // mouse button already released
        };
        frame.MouseRightButtonUp += (o, e) => { e.Handled = true; Activate(); menu.IsOpen = true; };
        menu.Closed += delegate { if (!frame.IsMouseOver) Fade(close, 0); };
        MouseWheel += (o, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                frame.Opacity = Math.Max(0.2, Math.Min(1, Math.Round((frame.Opacity + (e.Delta > 0 ? 0.1 : -0.1)) * 10) / 10));
                Say("Deckkraft " + Math.Round(frame.Opacity * 100) + " %");
            }
            else Zoom(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), true);
        };
        KeyDown += (o, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Escape) Close();
            else if (ctrl && e.Key == Key.C) Copy();
            else if (ctrl && e.Key == Key.S) SaveAs();
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) Zoom(1, false);
            else if (e.Key == Key.OemPlus || e.Key == Key.Add) Zoom(zoom * 1.1, false);
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) Zoom(zoom / 1.1, false);
            else return;
            e.Handled = true;
        };
        Closed += delegate { all.Remove(this); view.Source = null; };

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        _ = SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80);   // WS_EX_TOOLWINDOW: not in Alt+Tab
    }

    Border Item(string glyph, string label, string key, Action run)
    {
        var d = new DockPanel { LastChildFill = true };
        var g = new TextBlock { Text = glyph, FontSize = 13, FontFamily = IconFont, Width = 26, VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var k = new TextBlock { Text = key, FontSize = 11.5, FontFamily = UiFont, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        k.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(g, Dock.Left);
        DockPanel.SetDock(k, Dock.Right);
        var t = new TextBlock { Text = label, FontSize = 12.5, FontFamily = UiFont, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        d.Children.Add(g);
        d.Children.Add(k);
        d.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 12, 7), Child = d, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); };
        b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += delegate { menu.IsOpen = false; run(); };
        return b;
    }

    static Shapes.Rectangle Sep()
    {
        var r = new Shapes.Rectangle { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        r.SetResourceReference(Shapes.Shape.FillProperty, "Line");
        return r;
    }

    static void Fade(UIElement e, double to)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(120))));
    }

    // x, y = top-left corner of the image in device pixels.
    void Place(int x, int y)
    {
        Left = -20000;
        Top = -20000;
        view.Width = img.PixelWidth;
        view.Height = img.PixelHeight;
        Show();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x15);   // move to the target monitor first so the scale is right
        var src = HwndSource.FromHwnd(hwnd);
        sc = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
        Fit();
        UpdateLayout();
        int pad = (int)Math.Round(Pad * sc);
        SetWindowPos(hwnd, (IntPtr)(-1), x - pad, y - pad, 0, 0, 0x11);   // TOPMOST, NOSIZE|NOACTIVATE
        frame.Opacity = 0;
        frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140))));
    }

    // 100 % = one image pixel per screen pixel, as captured.
    void Fit()
    {
        view.Width = img.PixelWidth * zoom / sc;
        view.Height = img.PixelHeight * zoom / sc;
    }

    // Zooms; with the mouse the point under the pointer stays put, otherwise the top-left corner.
    void Zoom(double z, bool atMouse)
    {
        z = Math.Max(0.1, Math.Min(5, z));
        if (Math.Abs(z - 1) < 0.05) z = 1;   // snaps to 100 %
        GetWindowRect(hwnd, out RECT r);
        int pad = (int)Math.Round(Pad * sc);
        double ox = r.left + pad, oy = r.top + pad, ax = ox, ay = oy;
        if (atMouse)
        {
            GetCursorPos(out POINT p);
            ax = p.x;
            ay = p.y;
        }
        double k = z / zoom;
        zoom = z;
        Fit();
        UpdateLayout();
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(ax - (ax - ox) * k) - pad, (int)Math.Round(ay - (ay - oy) * k) - pad, 0, 0, 0x15);
        Say(Math.Round(zoom * 100) + " %");
    }

    void Say(string text)
    {
        chipText.Text = text;
        chip.Visibility = Visibility.Visible;
        chipTimer.Stop();
        chipTimer.Start();
    }

    void Copy()
    {
        Shot.ToClipboard(img, Shot.Encode(img, false));
        Say("Kopiert");
    }

    void SaveAs()
    {
        string? saved = Shot.SaveAs(this, img, name);
        if (saved != null) Say(saved.Length > 0 ? "Gespeichert" : "Konnte nicht speichern");
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
