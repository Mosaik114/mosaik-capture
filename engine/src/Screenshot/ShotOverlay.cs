using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

// Vollbild ueber alle Monitore mit dem eingefrorenen Bild. Kein durchsichtiges Fenster
// (das waere bei 5120 x 1440 Pixeln zu traege); stattdessen wird es erst gezeigt, wenn
// es fertig gezeichnet ist (DWM-Cloak), dann blendet nur die Abdunklung weich ein.
public class ShotOverlay : Window
{
    const double Loupe = 130;   // Durchmesser der Lupe
    const int Cells = 13;       // Pixel pro Zeile in der Lupe
    const double Drag = 4;      // ab so vielen Pixeln Bewegung wird aus dem Klick ein Aufziehen

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

    readonly Action<ShotData> done;
    readonly IntPtr hwnd;
    readonly double s;   // Geraetepixel pro WPF-Einheit
    readonly Canvas root, ui, lupe;
    readonly Image shot, zoom;
    readonly System.Windows.Shapes.Path dim;
    readonly System.Windows.Shapes.Rectangle frame, center;
    readonly System.Windows.Shapes.Rectangle swatch;
    readonly System.Windows.Shapes.Line hLine, vLine;
    readonly Border sizeChip, info, hint;
    readonly TextBlock sizeText, hexText, posText, hintText;

    BitmapSource src;
    int vx, vy, vw, vh;                 // virtueller Bildschirm in Geraetepixeln
    List<Rect> wins = new List<Rect>(); // sichtbare Fenster von oben nach unten, relativ zu vx/vy
    List<RECT> mons = new List<RECT>(), works = new List<RECT>();
    Point start;
    bool pressed, dragging;
    Rect sel = Rect.Empty;
    string hex = "";
    bool recording;   // Bereich fuer eine Bildschirmaufnahme waehlen statt Screenshot

    // Aufnahme: gewaehlter Bereich in Geraetepixeln (absolut), dazu Monitor und Arbeitsbereich
    public Action<Int32Rect, RECT, RECT> Recorded;
    public Action Aborted;

    public ShotOverlay(Action<ShotData> onDone)
    {
        done = onDone;
        Title = "Mosaiks-Screenshot";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Palette.Apply(Resources);

        root = new Canvas();
        shot = new Image { Stretch = Stretch.Fill };
        root.Children.Add(shot);
        ui = new Canvas { IsHitTestVisible = false };
        root.Children.Add(ui);

        dim = new System.Windows.Shapes.Path { Fill = Frozen(0xA6, 0x14, 0x14, 0x13) };
        ui.Children.Add(dim);
        var guide = Frozen(0x66, 0xD9, 0x77, 0x57);
        hLine = new System.Windows.Shapes.Line { Stroke = guide, StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new double[] { 4, 4 }) };
        vLine = new System.Windows.Shapes.Line { Stroke = guide, StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new double[] { 4, 4 }) };
        ui.Children.Add(hLine);
        ui.Children.Add(vLine);
        frame = new System.Windows.Shapes.Rectangle { StrokeThickness = 2 };
        frame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        ui.Children.Add(frame);
        sizeText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sizeChip = Chip(sizeText);
        ui.Children.Add(sizeChip);

        // Lupe: vergroesserte Pixel mit Raster, der Pixel unter dem Zeiger ist umrandet
        double z = Loupe / Cells;
        lupe = new Canvas { Width = Loupe + 6, Height = Loupe + 6 };
        var back = new System.Windows.Shapes.Ellipse
        {
            Width = Loupe + 6, Height = Loupe + 6,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.5 }
        };
        back.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Bg");
        lupe.Children.Add(back);
        var cells = new Canvas { Width = Loupe, Height = Loupe, Clip = new EllipseGeometry(new Point(Loupe / 2, Loupe / 2), Loupe / 2, Loupe / 2) };
        zoom = new Image { Width = Loupe, Height = Loupe, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
        cells.Children.Add(zoom);
        var grid = Frozen(0x22, 0xFA, 0xF9, 0xF5);
        for (int i = 1; i < Cells; i++)
        {
            cells.Children.Add(new System.Windows.Shapes.Line { X1 = i * z, X2 = i * z, Y1 = 0, Y2 = Loupe, Stroke = grid, StrokeThickness = 1 });
            cells.Children.Add(new System.Windows.Shapes.Line { Y1 = i * z, Y2 = i * z, X1 = 0, X2 = Loupe, Stroke = grid, StrokeThickness = 1 });
        }
        center = new System.Windows.Shapes.Rectangle { Width = z + 2, Height = z + 2, StrokeThickness = 2 };
        center.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        cells.Children.Add(center);
        Canvas.SetLeft(cells, 3);
        Canvas.SetTop(cells, 3);
        lupe.Children.Add(cells);
        var rim = new System.Windows.Shapes.Ellipse { Width = Loupe + 2, Height = Loupe + 2, StrokeThickness = 1 };
        rim.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Line");
        Canvas.SetLeft(rim, 2);
        Canvas.SetTop(rim, 2);
        lupe.Children.Add(rim);
        ui.Children.Add(lupe);

        swatch = new System.Windows.Shapes.Rectangle { Width = 11, Height = 11, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Line");
        hexText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        hexText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        posText = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        posText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(hexText);
        row.Children.Add(posText);
        info = Chip(row);
        ui.Children.Add(info);

        hintText = new TextBlock
        {
            FontSize = 12.5, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Text = "Ziehen = Bereich   ·   Klick = Fenster   ·   Leertaste = ganzer Bildschirm   ·   C = Farbe kopieren   ·   Esc = abbrechen"
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        hint = Chip(hintText);
        hint.CornerRadius = new CornerRadius(10);
        hint.Padding = new Thickness(14, 8, 14, 8);
        hint.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.4 };
        ui.Children.Add(hint);
        Content = root;

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80);   // WS_EX_TOOLWINDOW: nicht in Alt+Tab
        s = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11;

        MouseMove += (o, e) => Moved(Px(e));
        MouseLeftButtonDown += (o, e) => { start = Px(e); pressed = true; dragging = false; CaptureMouse(); };
        MouseLeftButtonUp += (o, e) => Released(Px(e));
        MouseRightButtonDown += (o, e) =>
        {
            if (dragging) { pressed = dragging = false; ReleaseMouseCapture(); Moved(Px(e)); }   // Aufziehen verwerfen
            else Cancel();
        };
        PreviewKeyDown += (o, e) => Key(e);
        Deactivated += (o, e) => { if (IsVisible) Cancel(); };
    }

    internal static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    internal static Border Chip(UIElement child)
    {
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 4), BorderThickness = new Thickness(1), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "Bg");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    Point Px(MouseEventArgs e)
    {
        Point p = e.GetPosition(root);
        return new Point(Math.Floor(p.X * s), Math.Floor(p.Y * s));
    }

    public void Open() { Begin(false); }

    public void OpenForRecording() { Begin(true); }

    void Begin(bool rec)
    {
        if (IsVisible) return;
        recording = rec;
        hintText.Text = rec
            ? "Aufnahme:   Ziehen = Bereich   ·   Klick = Fenster   ·   Leertaste = ganzer Bildschirm   ·   Esc = abbrechen"
            : "Ziehen = Bereich   ·   Klick = Fenster   ·   Leertaste = ganzer Bildschirm   ·   C = Farbe kopieren   ·   Esc = abbrechen";
        Palette.Apply(Resources);
        vx = GetSystemMetrics(76); vy = GetSystemMetrics(77); vw = GetSystemMetrics(78); vh = GetSystemMetrics(79);
        src = Grab(vx, vy, vw, vh, 96 * s);   // vor dem Zeigen, damit nichts von uns mit drauf ist
        ListWindows();
        ListMonitors();

        shot.Source = src;
        shot.Width = root.Width = vw / s;
        shot.Height = root.Height = vh / s;
        dim.Data = null;
        frame.Visibility = sizeChip.Visibility = Visibility.Hidden;
        pressed = dragging = false;
        sel = Rect.Empty;
        ui.Opacity = 0;

        POINT c;
        GetCursorPos(out c);
        var cur = new Point(c.x - vx, c.y - vy);
        RECT m = MonitorAt(cur, mons);
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(hint, ((m.left + m.right) / 2.0 - vx) / s - hint.DesiredSize.Width / 2);
        Canvas.SetTop(hint, (m.bottom - vy) / s - 90);
        Moved(cur);

        // Erst unsichtbar zeigen und zeichnen lassen, dann aufdecken: kein schwarzes Aufblitzen
        Cloak(hwnd, true);
        Width = vw / s;
        Height = vh / s;
        Show();
        SetWindowPos(hwnd, (IntPtr)(-1), vx, vy, vw, vh, 0x0040);   // HWND_TOPMOST, SWP_SHOWWINDOW
        TakeFocus(this, hwnd);
        var reveal = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        reveal.Tick += delegate
        {
            reveal.Stop();
            if (!IsVisible) return;
            Cloak(hwnd, false);
            ui.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };
        reveal.Start();
    }

    // Ohne Auswahl: gleich der ganze Monitor, auf dem der Zeiger steht
    public void TakeMonitor()
    {
        if (IsVisible) return;
        vx = GetSystemMetrics(76); vy = GetSystemMetrics(77); vw = GetSystemMetrics(78); vh = GetSystemMetrics(79);
        src = Grab(vx, vy, vw, vh, 96 * s);
        ListMonitors();
        POINT c;
        GetCursorPos(out c);
        Finish(ToRect(MonitorAt(new Point(c.x - vx, c.y - vy), mons)));
    }

    // Zeiger bewegt: Fenster darunter hervorheben bzw. aufgezogenen Bereich zeigen, Lupe nachfuehren
    void Moved(Point p)
    {
        if (src == null) return;
        if (pressed && !dragging && (Math.Abs(p.X - start.X) > Drag || Math.Abs(p.Y - start.Y) > Drag)) dragging = true;
        if (dragging)
        {
            double w = Math.Abs(p.X - start.X) + 1, h = Math.Abs(p.Y - start.Y) + 1;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) w = h = Math.Max(w, h);   // Shift = Quadrat
            sel = new Rect(p.X < start.X ? start.X - w + 1 : start.X, p.Y < start.Y ? start.Y - h + 1 : start.Y, w, h);
        }
        else
        {
            sel = Rect.Empty;
            foreach (Rect r in wins) if (r.Contains(p)) { sel = r; break; }
        }
        Mark(sel);

        hLine.X1 = 0; hLine.X2 = vw / s; hLine.Y1 = hLine.Y2 = (p.Y + 0.5) / s;
        vLine.Y1 = 0; vLine.Y2 = vh / s; vLine.X1 = vLine.X2 = (p.X + 0.5) / s;
        hLine.Visibility = vLine.Visibility = sel.IsEmpty || dragging ? Visibility.Visible : Visibility.Hidden;
        Magnify(p);
    }

    // Abdunklung mit Loch, Rahmen und Groessenmarke fuer einen Bereich (Geraetepixel)
    void Mark(Rect r)
    {
        var full = new RectangleGeometry(new Rect(0, 0, vw / s, vh / s));
        if (r.IsEmpty)
        {
            dim.Data = full;
            frame.Visibility = sizeChip.Visibility = Visibility.Hidden;
            return;
        }
        var d = new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(full);
        g.Children.Add(new RectangleGeometry(d));
        dim.Data = g;
        frame.Width = d.Width + 2;
        frame.Height = d.Height + 2;
        Canvas.SetLeft(frame, d.X - 1);
        Canvas.SetTop(frame, d.Y - 1);
        sizeText.Text = (int)r.Width + " × " + (int)r.Height;
        Canvas.SetLeft(sizeChip, d.X);
        Canvas.SetTop(sizeChip, d.Y >= 36 ? d.Y - 34 : d.Y + 8);   // am oberen Rand nach innen
        frame.Visibility = sizeChip.Visibility = Visibility.Visible;
    }

    void Magnify(Point p)
    {
        int px = (int)p.X, py = (int)p.Y, half = Cells / 2;
        int x0 = Math.Max(0, Math.Min(src.PixelWidth - Cells, px - half));
        int y0 = Math.Max(0, Math.Min(src.PixelHeight - Cells, py - half));
        zoom.Source = new CroppedBitmap(src, new Int32Rect(x0, y0, Cells, Cells));
        double z = Loupe / Cells;
        Canvas.SetLeft(center, (px - x0) * z - 1);
        Canvas.SetTop(center, (py - y0) * z - 1);

        var pix = new byte[4];
        src.CopyPixels(new Int32Rect(Math.Min(px, src.PixelWidth - 1), Math.Min(py, src.PixelHeight - 1), 1, 1), pix, 4, 0);
        var col = Color.FromRgb(pix[2], pix[1], pix[0]);
        hex = "#" + pix[2].ToString("X2") + pix[1].ToString("X2") + pix[0].ToString("X2");
        swatch.Fill = new SolidColorBrush(col);
        hexText.Text = hex;
        posText.Text = (px + vx) + ", " + (py + vy);

        // rechts unter dem Zeiger, am Monitorrand auf die andere Seite
        RECT m = MonitorAt(p, mons);
        info.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lx = p.X / s + 24, ly = p.Y / s + 24, tall = Loupe + 12 + info.DesiredSize.Height;
        if (lx + Loupe + 6 > (m.right - vx) / s) lx = p.X / s - 24 - Loupe - 6;
        if (ly + tall > (m.bottom - vy) / s) ly = p.Y / s - 24 - tall;
        Canvas.SetLeft(lupe, lx);
        Canvas.SetTop(lupe, ly);
        Canvas.SetLeft(info, lx + (Loupe + 6 - info.DesiredSize.Width) / 2);
        Canvas.SetTop(info, ly + Loupe + 12);
    }

    void Released(Point p)
    {
        if (!pressed) return;
        pressed = false;
        ReleaseMouseCapture();
        if (dragging) { dragging = false; if (sel.Width >= 3 && sel.Height >= 3) Finish(sel); return; }
        // einfacher Klick: Fenster darunter, sonst der ganze Bildschirm
        Rect r = Rect.Empty;
        foreach (Rect w in wins) if (w.Contains(p)) { r = w; break; }
        if (r.IsEmpty) r = ToRect(MonitorAt(p, mons));
        Finish(r);
    }

    void Key(KeyEventArgs e)
    {
        POINT c;
        GetCursorPos(out c);
        var p = new Point(c.x - vx, c.y - vy);
        if (e.Key == System.Windows.Input.Key.Escape) Cancel();
        else if (e.Key == System.Windows.Input.Key.Space) Finish(ToRect(MonitorAt(p, mons)));
        else if (e.Key == System.Windows.Input.Key.Enter && !sel.IsEmpty && !dragging) Finish(sel);
        else if (e.Key == System.Windows.Input.Key.C && !recording)
        {
            try { Clipboard.SetText(hex); hexText.Text = hex + "  kopiert"; } catch (COMException) { }   // Zwischenablage gerade belegt: Hinweis bleibt aus
        }
        else if (e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift) Moved(p);
        e.Handled = true;
    }

    Rect ToRect(RECT m) { return new Rect(m.left - vx, m.top - vy, m.right - m.left, m.bottom - m.top); }

    void Finish(Rect r)
    {
        r.Intersect(new Rect(0, 0, vw, vh));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) { Cancel(); return; }
        var cut = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        if (recording)
        {
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            RECT mon = MonitorAt(c, mons), wk = MonitorAt(c, works);
            Dismiss();
            if (Recorded != null) Recorded(new Int32Rect(cut.X + vx, cut.Y + vy, cut.Width, cut.Height), mon, wk);
            return;
        }
        var crop = new CroppedBitmap(src, cut);
        crop.Freeze();
        // Karte auf den Monitor, auf dem der Bereich liegt
        var mid = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var d = new ShotData
        {
            Full = src, Image = crop, Cut = cut, Monitor = MonitorAt(mid, mons), Work = MonitorAt(mid, works),
            Vx = vx, Vy = vy, Vw = vw, Vh = vh, Scale = s
        };
        Dismiss();
        done(d);
    }

    void Cancel()
    {
        Dismiss();
        if (recording && Aborted != null) Aborted();
    }

    void Dismiss()
    {
        Cloak(hwnd, true);
        pressed = dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Hide();
        shot.Source = zoom.Source = null;
        src = null;   // das grosse Bild freigeben
    }

    RECT MonitorAt(Point p, List<RECT> list)
    {
        for (int i = 0; i < mons.Count; i++)
        {
            RECT m = mons[i];
            if (p.X + vx >= m.left && p.X + vx < m.right && p.Y + vy >= m.top && p.Y + vy < m.bottom) return list[i];
        }
        return list.Count > 0 ? list[0] : new RECT { left = vx, top = vy, right = vx + vw, bottom = vy + vh };
    }

    internal static void TakeFocus(Window w, IntPtr hwnd)
    {
        w.Activate();
        if (GetForegroundWindow() != hwnd)
        {
            // Windows gibt den Fokus nicht immer her: kurz an den Eingabe-Thread des aktiven Fensters koppeln
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            AttachThreadInput(me, other, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(me, other, false);
        }
        w.Focus();
        Keyboard.Focus(w);
    }

    internal static void Cloak(IntPtr hwnd, bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 13, ref v, 4);   // DWMWA_CLOAK
    }

    // Sichtbare Fenster in Z-Reihenfolge (oberstes zuerst), ohne Schatten, ohne eigene
    void ListWindows()
    {
        wins.Clear();
        uint own = (uint)Process.GetCurrentProcess().Id;
        var cls = new StringBuilder(64);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x20) != 0 || (ex & 0x80) != 0) return true;   // durchklickbare Einblendungen, Werkzeugfenster
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == own) return true;
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;   // z. B. andere Desktops
            cls.Length = 0;
            GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW") return true;   // Desktop = ganzer Bildschirm
            RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) GetWindowRect(h, out r);   // DWMWA_EXTENDED_FRAME_BOUNDS
            var rr = new Rect(r.left - vx, r.top - vy, Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));
            rr.Intersect(new Rect(0, 0, vw, vh));
            if (!rr.IsEmpty && rr.Width > 20 && rr.Height > 20) wins.Add(rr);
            return true;
        }, IntPtr.Zero);
    }

    void ListMonitors()
    {
        mons.Clear();
        works.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(m, ref mi)) { mons.Add(mi.rcMonitor); works.Add(mi.rcWork); }
            return true;
        }, IntPtr.Zero);
    }

    // Bildschirminhalt per GDI in ein fertiges WPF-Bild (Zeiger ist nicht mit drauf)
    internal static BitmapSource Grab(int x, int y, int w, int h, double dpi)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);   // SRCCOPY | CAPTUREBLT
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, px, ref bi, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        var img = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgr32, null, px, w * 4);
        img.Freeze();
        return img;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    delegate bool MonitorEnumProc(IntPtr m, IntPtr dc, ref RECT r, IntPtr d);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr d);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT v, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr srcDc, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
