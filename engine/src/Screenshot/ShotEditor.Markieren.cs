// Screenshot-Editor, Teil: Markierungen setzen (Weichzeichnen, Scheinwerfer, Textmarker, Verpixeln, Nummern, Text) (Felder und Aufbau in ShotEditor.cs)

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

partial class ShotEditor
{
    void Up()
    {
        if (mode == Mode.None) return;
        Mode m = mode;
        mode = Mode.None;
        ReleaseMouseCapture();
        if (m == Mode.Item)
        {
            var tt = itemMove;
            double x0 = itemX0, y0 = itemY0, x1 = tt.X, y1 = tt.Y;
            if (x0 != x1 || y0 != y1) Push(delegate { tt.X = x0; tt.Y = y0; }, delegate { tt.X = x1; tt.Y = y1; });
            RefreshShade(null);
        }
        else if (m == Mode.Frame || m == Mode.Resize)
        {
            Rect a = sel0, b = sel;
            if (a != b) Push(delegate { sel = a; Layout(); }, delegate { sel = b; Layout(); });
        }
        else if (m == Mode.Draw)
        {
            holdTimer.Stop();
            var el = cur;
            cur = null;
            ink.Children.Remove(el);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            Rect b = el.Data == null ? Rect.Empty : el.Data.Bounds;
            if (b.IsEmpty || (b.Width < 3 && b.Height < 3)) { RefreshShade(null); return; }   // nur geklickt
            if (tool == Tool.Pixel || tool == Tool.Blur)
            {
                var px = tool == Tool.Pixel ? Pixelate(b) : Blurred(b);
                if (px != null) Add(px);
            }
            else if (tool == Tool.Spot)
            {
                b.Intersect(sel);
                if (b.IsEmpty) { RefreshShade(null); return; }
                var spot = new System.Windows.Shapes.Path
                {
                    Data = new RectangleGeometry(new Rect(0, 0, b.Width, b.Height), 8, 8), Tag = "spot",
                    Stroke = Brushes.Transparent, StrokeThickness = 12, RenderTransform = new TranslateTransform()   // nur am Rand greifbar
                };
                Canvas.SetLeft(spot, b.X);
                Canvas.SetTop(spot, b.Y);
                Add(spot);
            }
            else if (tool == Tool.Marker && !snapped)
            {
                var pts = StrokePoints(el);
                var hl = TextHighlight(pts, Markers[width]);
                Add(hl ?? el);
            }
            else if (tool == Tool.Pen && !snapped)
            {
                var g = Smooth(StrokePoints(el));
                if (g != null) el.Data = g;
                Add(el);
            }
            else Add(el);
            snapped = false;
        }
    }

    // Weichzeichnen: Ausschnitt etwas groesser nehmen, damit der Rand nicht ins Durchsichtige verlaeuft, dann auf die Flaeche beschneiden
    FrameworkElement Blurred(Rect r)
    {
        r.Intersect(sel);
        if (r.IsEmpty) return null;
        int x = (int)Math.Round(r.X * s), y = (int)Math.Round(r.Y * s);
        int w = Math.Min((int)Math.Round(r.Width * s), data.Full.PixelWidth - x), h = Math.Min((int)Math.Round(r.Height * s), data.Full.PixelHeight - y);
        if (w < 2 || h < 2) return null;
        const double radius = 14;
        int pad = (int)Math.Ceiling(radius * 2 * s);
        int px = Math.Max(0, x - pad), py = Math.Max(0, y - pad);
        int pr = Math.Min(data.Full.PixelWidth, x + w + pad), pb = Math.Min(data.Full.PixelHeight, y + h + pad);
        var crop = new CroppedBitmap(data.Full, new Int32Rect(px, py, pr - px, pb - py));
        crop.Freeze();
        var img = new Image
        {
            Source = crop, Width = (pr - px) / s, Height = (pb - py) / s, Stretch = Stretch.Fill,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality }
        };
        Canvas.SetLeft(img, (px - x) / s);
        Canvas.SetTop(img, (py - y) / s);
        var c = new Canvas { Width = w / s, Height = h / s, ClipToBounds = true, RenderTransform = new TranslateTransform(), Tag = "cover" };
        c.Children.Add(img);
        Canvas.SetLeft(c, x / s);
        Canvas.SetTop(c, y / s);
        return c;
    }

    // Abdunklung fuer alle Scheinwerfer: eine Flaeche mit Loechern, liegt unter allen anderen Markierungen
    void RefreshShade(Rect? extra)
    {
        Geometry g = null;
        var holes = new List<Geometry>();
        foreach (UIElement c in ink.Children)
        {
            var sp = c as System.Windows.Shapes.Path;
            if (sp == null || !"spot".Equals(sp.Tag)) continue;
            var tt = (TranslateTransform)sp.RenderTransform;
            Rect r = sp.Data.Bounds;
            r.Offset(Canvas.GetLeft(sp) + tt.X, Canvas.GetTop(sp) + tt.Y);
            holes.Add(new RectangleGeometry(r, 8, 8));
        }
        if (extra.HasValue) holes.Add(new RectangleGeometry(extra.Value, 8, 8));
        if (holes.Count == 0)
        {
            if (shade != null) ink.Children.Remove(shade);
            return;
        }
        g = new RectangleGeometry(new Rect(0, 0, root.Width, root.Height));
        foreach (var h in holes) g = Geometry.Combine(g, h, GeometryCombineMode.Exclude, null);
        if (shade == null) shade = new System.Windows.Shapes.Path { Fill = ShotOverlay.Frozen(0x99, 0x14, 0x14, 0x13), IsHitTestVisible = false };
        shade.Data = g;
        ink.Children.Remove(shade);
        int under = 0;
        foreach (UIElement c in ink.Children) if ("cover".Equals(((FrameworkElement)c).Tag)) under++;
        ink.Children.Insert(under, shade);
    }

    // Textmarker ueber Text: auf Zeilenhoehe und Wortgrenzen legen; null, wenn dort kein Text erkennbar ist
    FrameworkElement TextHighlight(List<Point> pts, double thick)
    {
        Rect b = Bounds(pts);
        if (b.Width < 12 || b.Height > b.Width * 0.35) return null;   // nur fast waagerechte Striche
        Rect dev = SnapToText(data.Full, b.Left * s, b.Right * s, (b.Top + b.Bottom) / 2 * s, thick / 2 * s);
        if (dev.IsEmpty) return null;
        double m = 2;
        Rect r = new Rect(dev.X / s - m, dev.Y / s - m, dev.Width / s + 2 * m, dev.Height / s + 2 * m);
        r.Intersect(sel);
        if (r.IsEmpty) return null;
        var hl = new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(0, 0, r.Width, r.Height), 3, 3), Fill = MarkerBrush(), RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(hl, r.X);
        Canvas.SetTop(hl, r.Y);
        return hl;
    }

    // Sucht im Bild (Geraetepixel) die Textzeile unter einem waagerechten Strich von x0 bis x1 auf Hoehe yc.
    // Hintergrund = haeufigste Farbe; Schrift = alles, was deutlich davon abweicht. Liefert die Woerter, die der Strich trifft.
    internal static Rect SnapToText(BitmapSource full, double x0, double x1, double yc, double half)
    {
        int W = full.PixelWidth, H = full.PixelHeight;
        double reach = Math.Max(half * 3, 30);
        int top = Math.Max(0, (int)(yc - reach)), bot = Math.Min(H, (int)(yc + reach));
        int left = Math.Max(0, (int)(x0 - reach * 3)), right = Math.Min(W, (int)(x1 + reach * 3));
        int bw = right - left, bh = bot - top;
        if (bw < 4 || bh < 8) return Rect.Empty;
        var src = new FormatConvertedBitmap(new CroppedBitmap(full, new Int32Rect(left, top, bw, bh)), PixelFormats.Bgr32, null, 0);
        var px = new byte[bw * bh * 4];
        src.CopyPixels(px, bw * 4, 0);

        // Hintergrundfarbe: haeufigste Farbe (5 Bit je Kanal)
        var hist = new int[32768];
        for (int i = 0; i < px.Length; i += 4) hist[(px[i] >> 3) | (px[i + 1] >> 3) << 5 | (px[i + 2] >> 3) << 10]++;
        int mode = 0;
        for (int i = 1; i < hist.Length; i++) if (hist[i] > hist[mode]) mode = i;
        if (hist[mode] < bw * bh * 0.3) return Rect.Empty;   // kein ruhiger Hintergrund, eher ein Foto
        int bB = (mode & 31) * 8 + 4, bG = (mode >> 5 & 31) * 8 + 4, bR = (mode >> 10) * 8 + 4;
        var inkPx = new bool[bw * bh];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int i = (y * bw + x) * 4;
                int d = Math.Max(Math.Abs(px[i] - bB), Math.Max(Math.Abs(px[i + 1] - bG), Math.Abs(px[i + 2] - bR)));
                inkPx[y * bw + x] = d > 60;
            }

        // Zeilen: welche Reihen haben unter dem Strich Schrift
        int sx0 = Math.Max(0, (int)x0 - left), sx1 = Math.Min(bw, (int)x1 - left);
        var rowInk = new bool[bh];
        for (int y = 0; y < bh; y++)
            for (int x = sx0; x < sx1; x++)
                if (inkPx[y * bw + x]) { rowInk[y] = true; break; }
        int cy = (int)yc - top, lt = -1, lb = -1;
        double bestDist = double.MaxValue;
        for (int y = 0; y < bh; )
        {
            if (!rowInk[y]) { y++; continue; }
            int a = y;
            while (y < bh && (rowInk[y] || (y + 1 < bh && rowInk[y + 1]))) y++;   // eine Leerreihe innerhalb der Zeile ueberbruecken
            int z = y - 1;
            double dist = cy < a ? a - cy : cy > z ? cy - z : 0;
            if (dist < bestDist) { bestDist = dist; lt = a; lb = z; }
        }
        if (lt < 0 || bestDist > half + 2) return Rect.Empty;
        int core = lb - lt + 1;
        if (core < 4) return Rect.Empty;

        // Zeile nach oben und unten vervollstaendigen: trifft der Strich nur kleine Buchstaben (w, i, c), fehlen
        // Oberlaengen, i-Punkte und Unterlaengen. Gesucht wird in einem breiteren Band, kleine Luecken (i-Punkt)
        // werden ueberbrueckt; hoechstens gut doppelt so hoch wie der Kern, damit keine Nachbarzeile dazukommt.
        int bx0 = Math.Max(0, sx0 - 2 * core), bx1 = Math.Min(bw, sx1 + 2 * core);
        int gap = Math.Max(2, (int)Math.Round(core * 0.25)), maxH = (int)(core * 2.2);
        for (int y = lt - 1, blank = 0; y >= 0 && blank <= gap && lb - y + 1 <= maxH; y--)
            if (AnyInk(inkPx, bw, y, bx0, bx1)) { lt = y; blank = 0; } else blank++;
        for (int y = lb + 1, blank = 0; y < bh && blank <= gap && y - lt + 1 <= maxH; y++)
            if (AnyInk(inkPx, bw, y, bx0, bx1)) { lb = y; blank = 0; } else blank++;
        int lh = lb - lt + 1;
        if (lt == 0 || lb == bh - 1) return Rect.Empty;   // reicht bis zum Rand: keine Textzeile

        // Spalten dieser Zeile: Woerter sind Schrift-Laeufe, kleine Luecken (Buchstabenabstand) gehoeren dazu.
        // Buchstaben stehen bis etwa 0,15 Schriftgroesse auseinander, Woerter ab etwa 0,3 (Leerzeichen).
        var colInk = new bool[bw];
        for (int x = 0; x < bw; x++)
            for (int y = lt; y <= lb; y++)
                if (inkPx[y * bw + x]) { colInk[x] = true; break; }
        int join = Math.Max(2, (int)Math.Round(lh * 0.27));
        int wl = -1, wr = -1;
        for (int x = 0; x < bw; )
        {
            if (!colInk[x]) { x++; continue; }
            int a = x, lastInk = x;
            while (x < bw && x - lastInk <= join) { if (colInk[x]) lastInk = x; x++; }
            int z = lastInk;
            double over = Math.Min(z + 1, sx1) - Math.Max(a, sx0);
            if (over > 0.25 * (z - a + 1) || (over > 0 && sx1 - sx0 < z - a + 1 && over >= 0.6 * (sx1 - sx0)))
            {
                if (wl < 0) wl = a;
                wr = z;
            }
        }
        if (wl < 0 || wl == 0 || wr == bw - 1) return Rect.Empty;
        return new Rect(left + wl, top + lt, wr - wl + 1, lh);
    }

    static bool AnyInk(bool[] inkPx, int bw, int y, int x0, int x1)
    {
        for (int x = x0; x < x1; x++) if (inkPx[y * bw + x]) return true;
        return false;
    }

    // Bereich grob verpixeln: verkleinern und mit harten Kanten wieder aufziehen
    FrameworkElement Pixelate(Rect r)
    {
        r.Intersect(sel);
        if (r.IsEmpty) return null;
        int x = (int)Math.Round(r.X * s), y = (int)Math.Round(r.Y * s);
        int w = Math.Min((int)Math.Round(r.Width * s), data.Full.PixelWidth - x), h = Math.Min((int)Math.Round(r.Height * s), data.Full.PixelHeight - y);
        if (w < 2 || h < 2) return null;
        double block = Math.Max(6, Math.Round(11 * s));
        var crop = new CroppedBitmap(data.Full, new Int32Rect(x, y, w, h));
        var small = new TransformedBitmap(crop, new ScaleTransform(Math.Max(1, Math.Round(w / block)) / w, Math.Max(1, Math.Round(h / block)) / h));
        small.Freeze();
        var img = new Image { Source = small, Width = w / s, Height = h / s, Stretch = Stretch.Fill, RenderTransform = new TranslateTransform(), Tag = "cover" };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, x / s);
        Canvas.SetTop(img, y / s);
        return img;
    }

    int NextNumber()
    {
        int n = 1;
        foreach (UIElement c in ink.Children)
        {
            var fe = c as FrameworkElement;
            if (fe != null && "nr".Equals(fe.Tag)) n++;
        }
        return n;
    }

    // Ziffern als Geometrie, deren sichtbarer Umriss genau um den Punkt c liegt
    // (eine TextBlock-Zeile hat Ober- und Unterlaengen-Platz und sitzt deshalb nie ganz mittig)
    static Geometry Digits(string text, double em, Point c)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(DigitFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), em, Brushes.White, 1.0);
        var g = ft.BuildGeometry(new Point(0, 0));
        Rect b = g.Bounds;
        g.Transform = new TranslateTransform(c.X - b.X - b.Width / 2, c.Y - b.Y - b.Height / 2);
        g.Freeze();
        return g;
    }

    // Nummern-Punkt: Kreis, ab zwei Stellen eine Pille, Mittelpunkt bei (h/2, h/2) vom linken Rand aus gerechnet
    static FrameworkElement Badge(int n, Color col, double h)
    {
        string text = n.ToString();
        bool bright = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255 > 0.7;
        double em = Math.Round(h * 0.56 * 2) / 2;
        Geometry probe = Digits(text, em, new Point(0, 0));
        double w = text.Length < 2 ? h : Math.Round(Math.Max(h, probe.Bounds.Width + h * 0.62));
        var g = new Grid
        {
            Width = w, Height = h, Tag = "nr", RenderTransform = new TranslateTransform(),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 7, ShadowDepth = 1.5, Direction = 270, Opacity = 0.38 }
        };
        g.Children.Add(new Border
        {
            Background = new SolidColorBrush(col), CornerRadius = new CornerRadius(h / 2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)), BorderThickness = new Thickness(bright ? 1 : 0)   // helle Farben brauchen auf Weiss eine Kante
        });
        g.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Digits(text, em, new Point(w / 2, h / 2)),
            Fill = bright ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x24)) : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        return g;
    }

    void PlaceNumber(Point p)
    {
        double h = Dots[width];
        var g = Badge(NextNumber(), Inks[color], h);
        Canvas.SetLeft(g, Math.Round(p.X - g.Width / 2));
        Canvas.SetTop(g, Math.Round(p.Y - h / 2));
        Add(g);
    }

    // Vorschau der naechsten Nummer am Zeiger; true, wenn sie gerade sichtbar ist
    bool UpdateGhost()
    {
        bool show = tool == Tool.Number && mode == Mode.None && typing == null && sel.Contains(hover) && !bar.IsMouseOver;
        if (!show)
        {
            if (ghost != null) ghost.Visibility = Visibility.Hidden;
            return false;
        }
        int n = NextNumber();
        string key = n + "|" + color + "|" + width;
        if (key != ghostKey)
        {
            if (ghost != null) root.Children.Remove(ghost);
            ghost = Badge(n, Inks[color], Dots[width]);
            ghost.Opacity = 0.7;
            ghost.IsHitTestVisible = false;
            root.Children.Insert(root.Children.IndexOf(ink) + 1, ghost);
            ghostKey = key;
        }
        Canvas.SetLeft(ghost, Math.Round(hover.X - ghost.Width / 2));
        Canvas.SetTop(ghost, Math.Round(hover.Y - ghost.Height / 2));
        ghost.Visibility = Visibility.Visible;
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

    // Schild um den Text: Rundung und Innenabstand wachsen mit der Schriftgroesse
    static Thickness PlatePad(double fs) { return new Thickness(Math.Round(fs * 0.45), Math.Round(fs * 0.16), Math.Round(fs * 0.45), Math.Round(fs * 0.2)); }

    void StartText(Point p, bool plate)
    {
        double fs = TextSizes[width];
        var tb = new TextBox
        {
            FontFamily = UiFont, FontWeight = FontWeights.SemiBold, FontSize = fs,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), AcceptsReturn = true,
            MinWidth = 12, FocusVisualStyle = null
        };
        typing = tb;
        if (plate)
        {
            var host = new Border { Child = tb, Effect = PlateShadow(), Tag = "plate" };
            typingHost = host;
            StyleTyping();
            Canvas.SetLeft(host, p.X - host.Padding.Left);
            Canvas.SetTop(host, p.Y - fs * 0.7 - host.Padding.Top);
        }
        else
        {
            tb.RenderTransform = new TranslateTransform();
            tb.Effect = TextShadow();
            typingHost = tb;
            StyleTyping();
            Canvas.SetLeft(tb, p.X - 3);
            Canvas.SetTop(tb, p.Y - fs * 0.7);
        }
        ink.Children.Add(typingHost);
        tb.Focus();
        Keyboard.Focus(tb);
    }

    // Farbe und Groesse auf das offene Eingabefeld anwenden
    void StyleTyping()
    {
        var tb = typing;
        double fs = TextSizes[width];
        tb.FontSize = fs;
        var host = typingHost as Border;
        if (host != null)
        {
            Color c = Inks[color];
            host.Background = new SolidColorBrush(c);
            host.CornerRadius = new CornerRadius(Math.Round(fs * 0.3));
            host.Padding = PlatePad(fs);
            tb.Foreground = tb.CaretBrush = Bright(c) ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x24)) : Brushes.White;
        }
        else tb.Foreground = tb.CaretBrush = new SolidColorBrush(Inks[color]);
    }

    // Texteingabe abschliessen: aus dem Eingabefeld wird ein fester Text an derselben Stelle
    void Commit()
    {
        var tb = typing;
        if (tb == null) return;
        var hostEl = typingHost;
        typing = null;
        typingHost = null;
        Rect first = tb.GetRectFromCharacterIndex(0);
        int idx = ink.Children.IndexOf(hostEl);
        ink.Children.Remove(hostEl);
        Keyboard.Focus(this);
        if (tb.Text.Trim().Length == 0 || idx < 0) return;
        double fx = first.IsEmpty ? 2 : first.X, fy = first.IsEmpty ? 0 : first.Y;
        var t = new TextBlock { Text = tb.Text, FontFamily = tb.FontFamily, FontWeight = tb.FontWeight, FontSize = tb.FontSize, Foreground = tb.Foreground };
        FrameworkElement el;
        var plate = hostEl as Border;
        if (plate != null)
        {
            // gleicher Abstand links und rechts, damit das Schild ausgewogen sitzt
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
        ink.Children.Insert(idx, el);
        Push(delegate { ink.Children.Remove(el); }, delegate { ink.Children.Add(el); });
    }

    // Oberste Markierung unter dem Zeiger (mit etwas Spielraum, damit duenne Linien treffbar sind)
    FrameworkElement ItemAt(Point p)
    {
        FrameworkElement found = null;
        VisualTreeHelper.HitTest(ink, null, r =>
        {
            DependencyObject o = r.VisualHit;
            while (o != null && VisualTreeHelper.GetParent(o) != ink) o = VisualTreeHelper.GetParent(o);
            found = o as FrameworkElement;
            return found != null ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(new EllipseGeometry(p, 5, 5)));
        return found != null && found.RenderTransform is TranslateTransform ? found : null;
    }
}
