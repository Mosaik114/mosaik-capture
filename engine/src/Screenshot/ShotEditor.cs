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

// Bearbeiten (Entwurf B): das eingefrorene Bild bleibt abgedunkelt stehen, der Ausschnitt ist hell,
// hat Anfasser zum Nachschneiden und darunter die Werkzeugleiste.
// Verwandte Werkzeuge teilen sich einen Knopf; ein zweiter Klick auf den aktiven Knopf zeigt die Auswahl.
// Tasten: V Verschieben, A Pfeil, R Rechteck, E Kreis, L Linie, P Stift, M Textmarker, S Scheinwerfer,
// T Text, B Text mit Hintergrund, N Nummer, X Verpixeln, W Weichzeichnen, 1-6 Farbe, +/- oder Mausrad Groesse,
// Strg+Z / Strg+Y, Entf, Strg+C kopieren, Strg+Umschalt+C Text kopieren, Strg+S speichern unter, Enter fertig, Esc abbrechen.
// Stift: am Ende kurz still halten = saubere Form; Textmarker: legt sich genau auf die ueberstrichene Textzeile.
public partial class ShotEditor : Window
{
    enum Tool { Move, Arrow, Box, Ellipse, Line, Pen, Marker, Spot, Text, Label, Number, Pixel, Blur }
    static readonly string[] ToolNames =
    {
        "Verschieben", "Pfeil", "Rechteck", "Kreis", "Linie", "Stift", "Textmarker", "Scheinwerfer",
        "Text", "Text mit Hintergrund", "Nummer", "Verpixeln", "Weichzeichnen"
    };
    static readonly Key[] ToolKeys = { Key.V, Key.A, Key.R, Key.E, Key.L, Key.P, Key.M, Key.S, Key.T, Key.B, Key.N, Key.X, Key.W };
    enum Mode { None, Draw, Item, Frame, Resize }
    class Step { public Action Undo, Redo; }
    class ToolGroup { public string Title; public Tool[] Tools; public Tool Current; public Border Btn; public Grid Host; public SolidColorBrush Ink; }

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
    readonly System.Windows.Shapes.Path dim;
    readonly System.Windows.Shapes.Rectangle frame;
    readonly Border[] handles = new Border[8];
    readonly Border sizeChip, bar, tip, note;
    readonly TextBlock sizeText, tipText, noteText;
    readonly StackPanel row;
    readonly List<ToolGroup> groups = new List<ToolGroup>();
    readonly Border menu;              // Auswahl der Werkzeuge eines Knopfs
    readonly StackPanel menuList;
    ToolGroup menuFor;
    readonly DispatcherTimer holdTimer;   // Stift/Textmarker kurz still gehalten
    Point holdAt;
    bool snapped;                         // Strich ist schon zur sauberen Form geworden
    Geometry snapShape;
    Point snapCenter, snapStart;
    double snapReach;
    bool snapOpen, snapArrow;
    System.Windows.Shapes.Path shade;     // Abdunklung fuer den Scheinwerfer
    FrameworkElement typingHost;          // Eingabefeld selbst oder die Flaeche drumherum
    readonly List<System.Windows.Shapes.Ellipse> colorRings = new List<System.Windows.Shapes.Ellipse>();
    readonly List<Border> widthBtn = new List<Border>();
    readonly List<Step> undo = new List<Step>(), redo = new List<Step>();
    readonly DispatcherTimer noteTimer;

    ShotData data;
    double s = 1;          // Geraetepixel pro WPF-Einheit
    Rect sel;              // Ausschnitt in WPF-Einheiten
    Tool tool = Tool.Arrow;
    int color, width = 2;
    FrameworkElement ghost;   // halbdurchsichtige Vorschau der naechsten Nummer am Zeiger
    string ghostKey;
    Point hover;
    Mode mode;
    Point p0;
    Rect sel0;
    int handle;
    System.Windows.Shapes.Path cur;
    PolyLineSegment line;
    FrameworkElement picked;
    TranslateTransform itemMove;
    double itemX0, itemY0;
    TextBox typing;
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
        dim = new System.Windows.Shapes.Path { Fill = ShotOverlay.Frozen(0xA6, 0x14, 0x14, 0x13), IsHitTestVisible = false };
        root.Children.Add(dim);
        ink = new Canvas();
        root.Children.Add(ink);
        frame = new System.Windows.Shapes.Rectangle { StrokeThickness = 2, IsHitTestVisible = false };
        frame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        root.Children.Add(frame);
        for (int i = 0; i < 8; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, StrokeThickness = 2 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text");
            dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
            var h = new Border { Width = 19, Height = 19, Background = Brushes.Transparent, Child = dot, Cursor = HCursor[i] };   // groessere Griff-Flaeche
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

        // Werkzeugleiste
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
            var ring = new System.Windows.Shapes.Ellipse { Width = 24, Height = 24, StrokeThickness = 1.5, Visibility = Visibility.Hidden };
            ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
            g.Children.Add(ring);
            g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Inks[i]) });
            colorRings.Add(ring);
            int k = i;
            g.MouseLeftButtonUp += delegate { SetColor(k); };
            g.MouseEnter += delegate { ShowTip(g, "Farbe   " + (k + 1)); };
            g.MouseLeave += delegate { tip.Visibility = Visibility.Hidden; };
            row.Children.Add(g);
        }
        var widths = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        double[] dots = { 4, 6, 8, 10, 12 };   // gerade Groessen, damit sie im 24er-Feld genau mittig sitzen
        for (int i = 0; i < dots.Length; i++)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = dots[i], Height = dots[i] };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text");
            var b = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Child = e, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            int k = i;
            b.MouseLeftButtonUp += delegate { SetWidth(k); };
            b.MouseEnter += delegate { Paint(b, true); ShowTip(b, WidthNames[k] + "   + / −"); };
            b.MouseLeave += delegate { Paint(b, false); tip.Visibility = Visibility.Hidden; };
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
            CornerRadius = new CornerRadius(12), Padding = new Thickness(6), BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.5 }
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Bg");
        bar.SetResourceReference(Border.BorderBrushProperty, "Line");
        bar.MouseLeftButtonDown += (o, e) => e.Handled = true;   // Klicks auf die Leiste zeichnen nichts
        root.Children.Add(bar);

        menuList = new StackPanel();
        menu = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(4), BorderThickness = new Thickness(1), Child = menuList, Cursor = Cursors.Arrow,
            MinWidth = 196, Visibility = Visibility.Hidden,
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
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80);   // WS_EX_TOOLWINDOW

        MouseLeftButtonDown += (o, e) => Down(e.GetPosition(root));
        MouseMove += (o, e) => Moved(e.GetPosition(root));
        MouseLeftButtonUp += (o, e) => Up();
        MouseLeave += delegate { hover = new Point(-1e6, -1e6); UpdateGhost(); };
        MouseWheel += (o, e) => { if (typing == null && mode == Mode.None) StepWidth(e.Delta > 0 ? 1 : -1); };
        PreviewKeyDown += (o, e) => OnKey(e);
    }

    // ---------- Leiste ----------

    TextBlock Glyph(string g, Brush b)
    {
        return new TextBlock { Text = g, FontSize = 16, FontFamily = IconFont, Foreground = b, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    void Sep()
    {
        var r = new System.Windows.Shapes.Rectangle { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Line");
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
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(1, 0, 1, 0),
            Child = content, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = primary ? "primary" : null
        };
        if (primary) b.SetResourceReference(Border.BackgroundProperty, "Accent");
        b.MouseEnter += delegate { Paint(b, true); ShowTip(b, label); };
        b.MouseLeave += delegate { Paint(b, false); tip.Visibility = Visibility.Hidden; };
        b.MouseLeftButtonUp += delegate { run(); };
        return b;
    }

    // Knopf fuer ein Werkzeug oder eine Gruppe verwandter Werkzeuge; die Gruppe zeigt ihr zuletzt gewaehltes
    void AddGroup(string title, params Tool[] tools)
    {
        var grp = new ToolGroup { Title = title, Tools = tools, Current = tools[0], Ink = new SolidColorBrush(Colors.White), Host = new Grid() };
        UIElement content = grp.Host;
        if (tools.Length > 1)
        {
            // kleines Dreieck unten rechts: hier gibt es mehr
            var more = new Grid { Width = 36, Height = 36 };
            more.Children.Add(grp.Host);
            var tri = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 30,26 L 30,30 L 26,30 Z"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            tri.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Muted");
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

    void ShowIcon(ToolGroup g)
    {
        g.Host.Children.Clear();
        g.Host.Children.Add(ToolIcon(g.Current, g.Ink));
    }

    // Auswahlmenue einer Gruppe: Symbol, Name und Taste, direkt am Knopf
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
        menu.InvalidateMeasure();   // sonst bleibt die Hoehe vom letzten Oeffnen stehen
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

    // Symbole: Schrift-Symbole wo es passende gibt, sonst im gleichen Strich selbst gezeichnet
    UIElement ToolIcon(Tool t, SolidColorBrush fg)
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
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2.5,13.5 L 13.5,2.5"), Stroke = fg, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        else if (t == Tool.Spot)
        {
            // abgedunkelte Flaeche mit hellem Loch
            var hole = new EllipseGeometry(new Point(8, 8), 4.5, 4.5);
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Combine(new RectangleGeometry(new Rect(1, 1, 14, 14), 2.5, 2.5), hole, GeometryCombineMode.Exclude, null), Fill = fg, Opacity = 0.45 });
            c.Children.Add(new System.Windows.Shapes.Path { Data = new EllipseGeometry(new Point(8, 8), 4.5, 4.5), Stroke = fg, StrokeThickness = 1.25 });
        }
        else if (t == Tool.Label)
        {
            // gefuelltes Schild, das A ausgestanzt
            var plate = new RectangleGeometry(new Rect(0, 1.5, 16, 13), 3, 3);
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Combine(plate, Digits("A", 11, new Point(8, 8)), GeometryCombineMode.Exclude, null), Fill = fg });
        }
        else if (t == Tool.Blur)
        {
            // Tropfen wie beim Weichzeichner ueblich
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 8,1.5 C 8,1.5 3,7.4 3,10.3 A 5,5 0 0 0 13,10.3 C 13,7.4 8,1.5 8,1.5 Z"), Stroke = fg, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 5.6,10.6 A 2.6,2.6 0 0 0 7.6,12.7"), Stroke = fg, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.6 });
        }
        else if (t == Tool.Number)
        {
            // Kreis und Ziffer als Geometrie, die Ziffer nach ihrem sichtbaren Umriss mittig gesetzt
            var g = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = fg, StrokeThickness = 1.5 });
            g.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Digits("1", 11.5, new Point(9, 9)), Fill = fg,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            });
            return g;
        }
        else
        {
            // 3x3 Kaestchen auf ganzen Pixeln: 4 breit, 1 Luecke
            var g = new Canvas { Width = 14, Height = 14, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 9; i++)
            {
                var r = new System.Windows.Shapes.Rectangle { Width = 4, Height = 4, Fill = fg, Opacity = i % 2 == 0 ? 1 : 0.4 };
                Canvas.SetLeft(r, i % 3 * 5);
                Canvas.SetTop(r, i / 3 * 5);
                g.Children.Add(r);
            }
            return g;
        }
        return c;
    }

    // Hintergrund eines Knopfs: aktiv = Sel, Maus drauf = Hover, sonst durchsichtig
    void Paint(Border b, bool hover)
    {
        string st = b.Tag as string;
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

    // Groesse per Mausrad oder +/- eine Stufe weiter
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
