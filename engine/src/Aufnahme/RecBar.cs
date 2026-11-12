using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

// ---------- Leiste waehrend der Aufnahme (verschiebbar, im Video unsichtbar) ----------

public class RecBar : Window
{
    const double Pad = 20, CapH = 36;
    public readonly IntPtr Handle;
    readonly Border pill, grip, caption, micBtn, soundBtn, clickBtn, pauseBtn, stopBtn, trashBtn, level;
    readonly TextBlock time, capText, micGlyph, soundGlyph, clickGlyph, pauseGlyph, trashGlyph;
    readonly Shapes.Ellipse dot;
    readonly Canvas capRow;
    readonly ScaleTransform levelScale = new ScaleTransform(0, 1);
    readonly DispatcherTimer tick, confirmTimer;
    readonly Dictionary<Border, Func<string>> captions = new Dictionary<Border, Func<string>>();
    bool confirm, dragging;
    int fromX, fromY, winX, winY;
    Border hovered;

    public RecBar()
    {
        RecUi.Setup(this, "Mosaiks-Aufnahme Leiste");
        SizeToContent = SizeToContent.WidthAndHeight;

        // Griff: roter Punkt und Zeit; hier laesst sich die Leiste verschieben
        dot = new Shapes.Ellipse { Width = 10, Height = 10, Fill = RecUi.Red, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        time = RecUi.Text(14, "Text");
        time.FontWeight = FontWeights.SemiBold;
        time.MinWidth = 40;
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        var gr = new StackPanel { Orientation = Orientation.Horizontal };
        gr.Children.Add(dot);
        gr.Children.Add(time);
        grip = new Border { Padding = new Thickness(12, 0, 10, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = gr };
        captions[grip] = () => "Verschieben";
        grip.MouseEnter += delegate { Hover(grip); };
        grip.MouseLeave += delegate { Hover(null); };
        grip.MouseLeftButtonDown += (o, e) => StartDrag();
        grip.MouseMove += (o, e) => DragTo();
        grip.MouseLeftButtonUp += (o, e) => { dragging = false; grip.ReleaseMouseCapture(); };

        micGlyph = RecUi.Glyph("", 16);
        level = new Border
        {
            Width = 14, Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5), RenderTransform = levelScale, RenderTransformOrigin = new Point(0.5, 0.5)
        };
        level.SetResourceReference(Border.BackgroundProperty, "Accent");
        var micBox = new Grid();
        micBox.Children.Add(micGlyph);
        micBox.Children.Add(level);
        micBtn = Button(micBox, () => Rec.Mic ? "Mikro wird aufgenommen" : "Mikro aus", delegate { Rec.SetMic(!Rec.Mic); Refresh(); });
        soundGlyph = RecUi.Glyph("", 16);
        soundBtn = Button(soundGlyph, () => Rec.Sound ? "Systemton wird aufgenommen" : "Systemton aus", delegate { Rec.SetSound(!Rec.Sound); Refresh(); });
        clickGlyph = RecUi.Glyph("\uE962", 16);
        clickBtn = Button(clickGlyph, () => Rec.Clicks ? "Klicks werden gezeigt" : "Klicks nicht zeigen", delegate { Rec.SetClicks(!Rec.Clicks); Refresh(); });
        pauseGlyph = RecUi.Glyph("", 16);
        pauseBtn = Button(pauseGlyph, () => Rec.Paused ? "Fortsetzen" : "Pause", delegate { Rec.TogglePause(); });

        var square = new Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 2.5, RadiusY = 2.5, Fill = RecUi.Light, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stopBtn = Button(square, () => "Beenden und speichern", delegate { Rec.Stop(); });
        stopBtn.Background = RecUi.Red;
        trashGlyph = RecUi.Glyph("\uE74D", 16);
        trashBtn = Button(trashGlyph, () => confirm ? "Nochmal klicken = verwerfen" : "Verwerfen", Trash);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(grip);
        row.Children.Add(Sep());
        row.Children.Add(micBtn);
        row.Children.Add(soundBtn);
        row.Children.Add(clickBtn);
        row.Children.Add(Sep());
        row.Children.Add(pauseBtn);
        row.Children.Add(stopBtn);
        row.Children.Add(Sep());
        row.Children.Add(trashBtn);
        pill = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(5), BorderThickness = new Thickness(1), Child = row,
            Effect = RecUi.Shadow(24, 6, 0.35)
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Bg");
        pill.SetResourceReference(Border.BorderBrushProperty, "Line");

        // Beschriftung der Knoepfe erscheint ueber der Leiste (innerhalb des Fensters, also auch nicht im Video)
        capText = RecUi.Text(12, "Text");
        caption = ShotOverlay.Chip(capText);
        caption.Visibility = Visibility.Hidden;
        capRow = new Canvas { Height = CapH };
        capRow.Children.Add(caption);

        var root = new Grid { Margin = new Thickness(Pad, 0, Pad, Pad) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CapH) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(capRow);
        Grid.SetRow(pill, 1);
        root.Children.Add(pill);
        Content = root;

        Handle = RecUi.Handle(this, RecUi.NoActivate | RecUi.Tool);
        RecUi.Exclude(Handle);
        tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        tick.Tick += delegate { Tick(); };
        confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        confirmTimer.Tick += delegate { confirmTimer.Stop(); confirm = false; Refresh(); };
    }

    Border Sep()
    {
        var b = new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, "Line");
        return b;
    }

    Border Button(UIElement child, Func<string> cap, Action run)
    {
        var b = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(1, 0, 1, 0), Child = child, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        captions[b] = cap;
        b.MouseEnter += delegate { Hover(b); };
        b.MouseLeave += delegate { Hover(null); };
        b.MouseLeftButtonUp += delegate { run(); if (hovered == b) Hover(b); };
        return b;
    }

    // Hervorheben und Beschriftung ueber dem Knopf zeigen
    void Hover(Border b)
    {
        hovered = b;
        Refresh();
        if (b == null) { caption.Visibility = Visibility.Hidden; return; }
        capText.Text = captions[b]();
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point p = b.TranslatePoint(new Point(b.ActualWidth / 2, 0), capRow);
        double w = caption.DesiredSize.Width, max = capRow.ActualWidth - w;
        Canvas.SetLeft(caption, Math.Round(Math.Max(0, Math.Min(max, p.X - w / 2))));
        Canvas.SetTop(caption, CapH - caption.DesiredSize.Height - 6);
        caption.Visibility = Visibility.Visible;
    }

    public void Present(Int32Rect area, ShotOverlay.RECT work)
    {
        Palette.Apply(Resources);
        confirm = false;
        hovered = null;
        caption.Visibility = Visibility.Hidden;
        Refresh();
        Tick();
        Left = -10000;
        Show();
        UpdateLayout();
        double s = RecUi.Scale(Handle);
        int pw = (int)Math.Round(pill.ActualWidth * s), ph = (int)Math.Round(pill.ActualHeight * s), gap = (int)Math.Round(14 * s), m = (int)Math.Round(10 * s);
        // unter den Bereich, sonst darueber, sonst innen an den unteren Rand (bei ganzem Bildschirm)
        int x = area.X + (area.Width - pw) / 2, y = area.Y + area.Height + gap;
        if (y + ph > work.bottom - m)
        {
            y = area.Y - gap - ph;
            if (y < work.top + m) y = Math.Min(area.Y + area.Height, work.bottom) - (int)Math.Round(24 * s) - ph;
        }
        x = Math.Max(work.left + m, Math.Min(work.right - m - pw, x));
        RecUi.Move(Handle, x - (int)Math.Round(Pad * s), y - (int)Math.Round(CapH * s));
        tick.Start();
    }

    public new void Hide()
    {
        tick.Stop();
        confirmTimer.Stop();
        dot.BeginAnimation(OpacityProperty, null);
        base.Hide();
    }

    // Zustand der Knoepfe
    public void Refresh()
    {
        bool paused = Rec.Paused;
        micGlyph.Text = Rec.Mic ? "\uE720" : "\uF781";
        soundGlyph.Text = Rec.Sound ? "\uE767" : "\uE74F";
        pauseGlyph.Text = paused ? "\uE768" : "\uE769";
        Paint(micGlyph, Rec.Mic ? "Text" : null);
        Paint(soundGlyph, Rec.Sound ? "Text" : null);
        Paint(clickGlyph, Rec.Clicks ? "Text" : "Muted");
        Paint(pauseGlyph, "Text");
        foreach (var b in new[] { micBtn, soundBtn, clickBtn, pauseBtn })
            if (b == hovered) b.SetResourceReference(Border.BackgroundProperty, "Hover"); else b.Background = Brushes.Transparent;
        stopBtn.Opacity = hovered == stopBtn ? 0.86 : 1;
        if (confirm) { trashBtn.Background = RecUi.Red; trashGlyph.Foreground = RecUi.Light; }
        else
        {
            if (hovered == trashBtn) trashBtn.SetResourceReference(Border.BackgroundProperty, "Hover"); else trashBtn.Background = Brushes.Transparent;
            Paint(trashGlyph, "Text");
        }
        // Punkt pulsiert, solange aufgenommen wird; pausiert steht er gedaempft still
        if (paused)
        {
            dot.BeginAnimation(OpacityProperty, null);
            dot.Opacity = 1;
            dot.SetResourceReference(Shapes.Shape.FillProperty, "Muted");
            time.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
        else
        {
            dot.Fill = RecUi.Red;
            time.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (IsVisible && !dot.HasAnimatedProperties)
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(800)))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }
        if (hovered != null && caption.Visibility == Visibility.Visible) capText.Text = captions[hovered]();
    }

    static void Paint(TextBlock t, string key)
    {
        if (key == null) t.Foreground = RecUi.Red; else t.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    void Tick()
    {
        time.Text = RecUi.Clock(Rec.Elapsed);
        double lvl = Rec.Mic && !Rec.Paused ? Math.Min(1, Math.Sqrt(Rec.MicLevel) * 1.2) : 0;
        levelScale.ScaleX = lvl < 0.04 ? 0 : lvl;
        if (!Rec.Paused && !dot.HasAnimatedProperties) Refresh();
    }

    // Erster Klick: rot markieren; zweiter Klick innerhalb von 3 s: verwerfen
    void Trash()
    {
        if (confirm) { confirm = false; confirmTimer.Stop(); Rec.Discard(); return; }
        confirm = true;
        confirmTimer.Stop();
        confirmTimer.Start();
        Refresh();
    }

    void StartDrag()
    {
        POINT p;
        GetCursorPos(out p);
        RECT r;
        GetWindowRect(Handle, out r);
        fromX = p.x; fromY = p.y; winX = r.left; winY = r.top;
        dragging = true;
        grip.CaptureMouse();
    }

    void DragTo()
    {
        if (!dragging) return;
        POINT p;
        GetCursorPos(out p);
        RecUi.Move(Handle, winX + p.x - fromX, winY + p.y - fromY);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
}
