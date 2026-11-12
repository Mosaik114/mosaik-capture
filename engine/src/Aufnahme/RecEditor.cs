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

// ---------- Kuerzen: Vorschau, Filmstreifen mit Anfang/Ende, als MP4 oder GIF speichern ----------
// Tasten: Leertaste abspielen/anhalten, I/O Anfang/Ende auf den Abspielkopf, Pfeile 0,1 s (mit Umschalt 1 s),
// Pos1/Ende springen, Enter speichern, Esc schliessen.

public class RecEditor : Window
{
    const double Inner = 880, HandleW = 14, TrackH = 64, StripY = 4, StripH = 56, MinSel = 0.3;
    readonly IntPtr hwnd;
    readonly Border card, videoBox, bigPlay, playBtn, soundBtn, actions, progressRow, hIn, hOut, selTop, selBot, knob;
    readonly MediaElement media;
    readonly Image poster, strip;
    readonly TextBlock title, sub, timeText, selText, playGlyph, soundGlyph, progText;
    readonly Canvas track;
    readonly Shapes.Rectangle dimL, dimR, headLine;
    readonly ScaleTransform progScale = new ScaleTransform(0, 1);
    readonly DispatcherTimer tick;
    RecResult res;
    ShotOverlay.RECT work;
    double dur = 1, a, b = 1, pos, grab;
    bool playing, sound, busy, exported;
    int drag, gen;

    internal bool Offscreen { get; set; }   // nur fuer Pruefbilder: Inhalt aufbauen, aber nichts zeigen

    public RecEditor()
    {
        RecUi.Setup(this, "Aufnahme kürzen");
        ShowActivated = true;
        Topmost = false;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        var st = new StackPanel { Width = Inner };

        // Kopf: Titel, Datei, Schliessen; hier laesst sich das Fenster verschieben
        title = new TextBlock { FontFamily = new FontFamily("Georgia"), FontSize = 18, Text = "Aufnahme kürzen" };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sub = RecUi.Text(12, "Muted");
        sub.Margin = new Thickness(0, 3, 0, 0);
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(sub);
        var close = Plain(RecUi.Glyph("\uE8BB", 11), 32, 16);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.MouseLeftButtonUp += delegate { Dismiss(); };
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(texts);
        head.MouseLeftButtonDown += (o, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };   // Maustaste schon wieder los
        st.Children.Add(head);

        // Video mit Vorschaubild, bis es geladen ist; Klick = abspielen/anhalten
        poster = new Image { Stretch = Stretch.Uniform };
        media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
        media.MediaOpened += delegate { poster.Visibility = Visibility.Hidden; Seek(pos); };
        media.MediaEnded += delegate { Pause(); Seek(b); };
        var bigGlyph = RecUi.Glyph("\uF5B0", 22);
        bigGlyph.Foreground = RecUi.Light;
        bigGlyph.Margin = new Thickness(4, 0, 0, 0);
        bigPlay = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(30), Child = bigGlyph, IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x13))
        };
        var vg = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        vg.Children.Add(poster);
        vg.Children.Add(media);
        vg.Children.Add(bigPlay);
        vg.MouseLeftButtonUp += delegate { TogglePlay(); };
        videoBox = new Border { Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(10), Child = vg, Background = ShotOverlay.Frozen(0xFF, 0x14, 0x14, 0x13) };
        st.Children.Add(videoBox);

        // Abspielen, Zeit, Laenge der Auswahl
        playGlyph = RecUi.Glyph("\uF5B0", 14);
        playBtn = Plain(playGlyph, 36, 18);
        playBtn.SetResourceReference(Border.BackgroundProperty, "Hover");
        playBtn.MouseLeftButtonUp += delegate { TogglePlay(); };
        timeText = RecUi.Text(13, "Text");
        timeText.Margin = new Thickness(12, 0, 0, 0);
        Typography.SetNumeralAlignment(timeText, FontNumeralAlignment.Tabular);
        selText = RecUi.Text(13, "Muted");
        Typography.SetNumeralAlignment(selText, FontNumeralAlignment.Tabular);
        var transport = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(selText, Dock.Right);
        transport.Children.Add(selText);
        transport.Children.Add(playBtn);
        transport.Children.Add(timeText);
        st.Children.Add(transport);

        // Zeitleiste: Filmstreifen, abgedunkelt ausserhalb der Auswahl, Griffe, Abspielkopf
        track = new Canvas { Width = Inner, Height = TrackH, Margin = new Thickness(0, 16, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var baseRect = new Shapes.Rectangle { Width = Inner - 2 * HandleW, Height = StripH };
        baseRect.SetResourceReference(Shapes.Shape.FillProperty, "Hover");
        Canvas.SetLeft(baseRect, HandleW);
        Canvas.SetTop(baseRect, StripY);
        track.Children.Add(baseRect);
        strip = new Image { Width = Inner - 2 * HandleW, Height = StripH, Stretch = Stretch.UniformToFill, ClipToBounds = true };   // nicht stauchen, Ueberstand abschneiden
        Canvas.SetLeft(strip, HandleW);
        Canvas.SetTop(strip, StripY);
        track.Children.Add(strip);
        var shade = ShotOverlay.Frozen(0xB8, 0x14, 0x14, 0x13);
        dimL = new Shapes.Rectangle { Height = StripH, Fill = shade };
        dimR = new Shapes.Rectangle { Height = StripH, Fill = shade };
        Canvas.SetTop(dimL, StripY);
        Canvas.SetTop(dimR, StripY);
        track.Children.Add(dimL);
        track.Children.Add(dimR);
        selTop = Bar(0);
        selBot = Bar(TrackH - StripY);
        hIn = Handle(new CornerRadius(5, 0, 0, 5));
        hOut = Handle(new CornerRadius(0, 5, 5, 0));
        headLine = new Shapes.Rectangle { Width = 2, Height = TrackH + 12, IsHitTestVisible = false };
        headLine.SetResourceReference(Shapes.Shape.FillProperty, "Text");
        Canvas.SetTop(headLine, -6);
        track.Children.Add(headLine);
        knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), IsHitTestVisible = false, BorderThickness = new Thickness(2) };
        knob.SetResourceReference(Border.BackgroundProperty, "Text");
        knob.SetResourceReference(Border.BorderBrushProperty, "Bg");
        Canvas.SetTop(knob, -12);
        track.Children.Add(knob);
        track.MouseLeftButtonDown += (o, e) => TrackDown(e.GetPosition(track).X);
        track.MouseMove += (o, e) => { if (drag != 0) TrackMove(e.GetPosition(track).X); };
        track.MouseLeftButtonUp += delegate { drag = 0; track.ReleaseMouseCapture(); };
        st.Children.Add(track);

        // Unten: Tastenhinweis, Ton, als GIF, speichern (oder waehrend des Speicherns der Fortschritt)
        var hint = RecUi.Text(11.5, "Muted");
        hint.Text = "Leertaste abspielen  ·  I / O Anfang / Ende  ·  ← → Schritt  ·  Enter speichern";
        soundGlyph = RecUi.Glyph("", 14);
        soundBtn = Pill(soundGlyph, "Ton", false);
        soundBtn.MouseLeftButtonUp += delegate { sound = !sound; media.IsMuted = !sound; Refresh(); };
        var gifBtn = Pill(RecUi.Glyph("\uF4A9", 14), "Als GIF", false);
        gifBtn.MouseLeftButtonUp += delegate { Export(true); };
        var saveBtn = Pill(RecUi.Glyph("\uE74E", 14), "Speichern", true);
        saveBtn.MouseLeftButtonUp += delegate { Export(false); };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow.Children.Add(soundBtn);
        btnRow.Children.Add(gifBtn);
        btnRow.Children.Add(saveBtn);
        var bottom = new DockPanel();
        DockPanel.SetDock(btnRow, Dock.Right);
        bottom.Children.Add(btnRow);
        bottom.Children.Add(hint);
        actions = new Border { Child = bottom };

        progText = RecUi.Text(12.5, "Text");
        var rail = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
        rail.SetResourceReference(Border.BackgroundProperty, "Line");
        var fill = new Border { CornerRadius = new CornerRadius(2), RenderTransform = progScale };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        rail.Child = fill;
        var pst = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pst.Children.Add(progText);
        pst.Children.Add(rail);
        progressRow = new Border { Child = pst, Visibility = Visibility.Collapsed };
        var foot = new Grid { Margin = new Thickness(0, 18, 0, 0), MinHeight = 34 };
        foot.Children.Add(actions);
        foot.Children.Add(progressRow);
        st.Children.Add(foot);

        card = new Border
        {
            Margin = new Thickness(24), Padding = new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Child = st, Effect = RecUi.Shadow(32, 10, 0.4)
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        Content = card;

        PreviewKeyDown += (o, e) => Key(e);
        hwnd = RecUi.Handle(this, 0);
        tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        tick.Tick += delegate { Follow(); };
    }

    Border Plain(TextBlock glyph, double size, double radius)
    {
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = glyph, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Sel"); };
        b.MouseLeave += delegate { if (b == playBtn) b.SetResourceReference(Border.BackgroundProperty, "Hover"); else b.Background = Brushes.Transparent; };
        return b;
    }

    // Knopf mit Symbol und Text; der Hauptknopf in Akzentfarbe
    Border Pill(TextBlock glyph, string label, bool primary)
    {
        var t = new TextBlock { Text = label, FontSize = 12.5, FontFamily = RecUi.Ui, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (primary) { t.FontWeight = FontWeights.SemiBold; t.Foreground = glyph.Foreground = RecUi.Light; }
        else
        {
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (glyph.Text.Length > 0) glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(glyph);
        row.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0, 14, 0), Height = 34, Margin = new Thickness(8, 0, 0, 0), Child = row, Cursor = Cursors.Hand };
        if (primary)
        {
            b.SetResourceReference(Border.BackgroundProperty, "Accent");
            b.MouseEnter += delegate { b.Opacity = 0.88; };
            b.MouseLeave += delegate { b.Opacity = 1; };
        }
        else
        {
            b.SetResourceReference(Border.BackgroundProperty, "Hover");
            b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Sel"); };
            b.MouseLeave += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); };
        }
        return b;
    }

    Border Bar(double y)
    {
        var r = new Border { Height = StripY, IsHitTestVisible = false };
        r.SetResourceReference(Border.BackgroundProperty, "Accent");
        Canvas.SetTop(r, y);
        track.Children.Add(r);
        return r;
    }

    Border Handle(CornerRadius corners)
    {
        var line = new Border { Width = 2, Height = 20, CornerRadius = new CornerRadius(1), Background = ShotOverlay.Frozen(0xCC, 0xFA, 0xF9, 0xF5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var h = new Border { Width = HandleW, Height = TrackH, CornerRadius = corners, Child = line, Cursor = Cursors.SizeWE };
        h.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(h);
        return h;
    }

    public void Open(RecResult r, ShotOverlay.RECT wk)
    {
        res = r;
        work = wk;
        dur = Math.Max(0.1, r.Duration);
        a = 0;
        b = dur;
        pos = 0;
        sound = r.Audio;
        busy = exported = false;
        playing = false;
        int g = ++gen;
        Palette.Apply(Resources);
        sub.Text = Path.GetFileName(r.Path) + " · " + RecUi.Clock(Math.Round(r.Duration)) + " · " + r.Width + " × " + r.Height;
        double vh = r.Width > 0 ? Inner * r.Height / r.Width : Inner * 9 / 16;
        videoBox.Height = Math.Round(Math.Max(180, Math.Min(495, vh)));
        videoBox.Width = Inner;
        videoBox.Clip = new RectangleGeometry(new Rect(0, 0, Inner, videoBox.Height), 10, 10);
        poster.Source = r.Thumb;
        poster.Visibility = Visibility.Visible;
        media.IsMuted = !sound;
        if (!Offscreen)
        {
            media.Source = new Uri(r.Path);
            media.Play();
            media.Pause();
        }
        soundBtn.Visibility = r.Audio ? Visibility.Visible : Visibility.Collapsed;
        actions.Visibility = Visibility.Visible;
        progressRow.Visibility = Visibility.Collapsed;
        strip.Source = null;
        Refresh();
        Draw();

        // Filmstreifen im Hintergrund: so viele Bilder, wie nebeneinander passen
        double aspect = r.Width > 0 && r.Height > 0 ? (double)r.Width / r.Height : 16.0 / 9;
        int n = Math.Max(2, Math.Min(24, (int)Math.Ceiling((Inner - 2 * HandleW) / (StripH * aspect))));
        string path = r.Path;
        Task.Factory.StartNew(() => RecResult.Strip(path, dur, n, (int)StripH * 2)).ContinueWith(t =>
        {
            if (t.IsFaulted) Program.Log("Filmstreifen", t.Exception);
            else if (g == gen) strip.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        if (Offscreen) return;
        Left = -10000;
        Show();
        UpdateLayout();
        double s = RecUi.Scale(hwnd);
        int pw = (int)Math.Round(ActualWidth * s), ph = (int)Math.Round(ActualHeight * s);
        SetWindowPos(hwnd, IntPtr.Zero, (work.left + work.right - pw) / 2, Math.Max(work.top, (work.top + work.bottom - ph) / 2), 0, 0, 0x1);   // SWP_NOSIZE
        ShotOverlay.TakeFocus(this, hwnd);
    }

    double X(double t) { return HandleW + t / dur * (Inner - 2 * HandleW); }
    double T(double x) { return Math.Max(0, Math.Min(dur, (x - HandleW) / (Inner - 2 * HandleW) * dur)); }

    void Draw()
    {
        double xa = Math.Round(X(a)), xb = Math.Round(X(b)), xp = Math.Round(X(pos));
        dimL.Width = Math.Max(0, xa - HandleW);
        Canvas.SetLeft(dimL, HandleW);
        dimR.Width = Math.Max(0, Inner - HandleW - xb);
        Canvas.SetLeft(dimR, xb);
        Canvas.SetLeft(hIn, xa - HandleW);
        Canvas.SetLeft(hOut, xb);
        selTop.Width = selBot.Width = Math.Max(0, xb - xa);
        Canvas.SetLeft(selTop, xa);
        Canvas.SetLeft(selBot, xa);
        Canvas.SetLeft(headLine, xp - 1);
        Canvas.SetLeft(knob, xp - 6);
        timeText.Text = RecUi.Fine(pos) + " / " + RecUi.Fine(dur);
        selText.Text = "Auswahl " + RecUi.Fine(b - a);
    }

    void Refresh()
    {
        playGlyph.Text = playing ? "\uE769" : "\uF5B0";
        playGlyph.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0);
        bigPlay.Visibility = playing ? Visibility.Hidden : Visibility.Visible;
        soundGlyph.Text = sound ? "\uE767" : "\uE74F";
        if (sound) soundGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Text"); else soundGlyph.Foreground = RecUi.Red;
    }

    void TrackDown(double x)
    {
        if (busy) return;
        double xa = X(a), xb = X(b);
        if (x >= xa - HandleW - 3 && x <= xa + 3) { drag = 1; grab = xa - x; }
        else if (x >= xb - 3 && x <= xb + HandleW + 3) { drag = 2; grab = xb - x; }
        else { drag = 3; grab = 0; }
        track.CaptureMouse();
        TrackMove(x);
    }

    // Griffe ziehen zeigt das Bild an der jeweiligen Stelle; sonst springt der Abspielkopf mit
    void TrackMove(double x)
    {
        double t = T(x + grab);
        if (drag == 1) { a = Math.Min(t, b - MinSel); a = Math.Max(0, a); Pause(); Seek(a); }
        else if (drag == 2) { b = Math.Max(t, a + MinSel); b = Math.Min(dur, b); Pause(); Seek(b); }
        else if (drag == 3) { Pause(); Seek(t); }
    }

    void Seek(double t)
    {
        pos = Math.Max(0, Math.Min(dur, t));
        media.Position = TimeSpan.FromSeconds(pos);
        Draw();
    }

    void TogglePlay()
    {
        if (busy) return;
        if (playing) Pause(); else Play();
    }

    void Play()
    {
        if (pos >= b - 0.05 || pos < a) Seek(a);
        media.Play();
        playing = true;
        tick.Start();
        Refresh();
    }

    void Pause()
    {
        if (!playing) return;
        media.Pause();
        playing = false;
        tick.Stop();
        pos = Math.Max(0, Math.Min(dur, media.Position.TotalSeconds));
        Refresh();
        Draw();
    }

    // Beim Abspielen: Kopf nachfuehren, am Ende der Auswahl anhalten
    void Follow()
    {
        pos = media.Position.TotalSeconds;
        if (pos >= b) { Pause(); Seek(b); return; }
        Draw();
    }

    void Key(KeyEventArgs e)
    {
        if (busy) { e.Handled = true; return; }
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 0.1;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Space: TogglePlay(); break;
            case System.Windows.Input.Key.I: Pause(); a = Math.Max(0, Math.Min(pos, b - MinSel)); Draw(); break;
            case System.Windows.Input.Key.O: Pause(); b = Math.Min(dur, Math.Max(pos, a + MinSel)); Draw(); break;
            case System.Windows.Input.Key.Left: Pause(); Seek(pos - step); break;
            case System.Windows.Input.Key.Right: Pause(); Seek(pos + step); break;
            case System.Windows.Input.Key.Home: Pause(); Seek(a); break;
            case System.Windows.Input.Key.End: Pause(); Seek(b); break;
            case System.Windows.Input.Key.Enter: Export(false); break;
            case System.Windows.Input.Key.Escape: Dismiss(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Export(bool gif)
    {
        if (busy) return;
        Pause();
        bool whole = a <= 0.01 && b >= dur - 0.01;
        if (!gif && whole && (sound || !res.Audio)) { Dismiss(); return; }   // nichts geaendert
        busy = true;
        actions.Visibility = Visibility.Collapsed;
        progressRow.Visibility = Visibility.Visible;
        string label = gif ? "GIF wird erstellt …" : "Wird gespeichert …";
        progText.Text = label;
        progText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        progScale.ScaleX = 0;
        var wk = work;
        Rec.Export(res, a, b, sound, gif, p => { progScale.ScaleX = p; progText.Text = label + "  " + (int)Math.Round(p * 100) + " %"; }, (r, err) =>
        {
            busy = false;
            if (r == null)
            {
                progText.Text = "Nicht gespeichert: " + err;
                progText.Foreground = RecUi.Red;
                actions.Visibility = Visibility.Visible;
                progressRow.Visibility = Visibility.Collapsed;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                progressRow.Visibility = Visibility.Visible;
                actions.Visibility = Visibility.Collapsed;
                t.Tick += delegate { t.Stop(); progressRow.Visibility = Visibility.Collapsed; actions.Visibility = Visibility.Visible; };
                t.Start();
                return;
            }
            exported = true;
            Dismiss();
            Rec.Exported(r, gif ? "GIF gespeichert und kopiert" : "Gekürzt und kopiert", wk);
        });
    }

    void Dismiss()
    {
        if (busy) return;
        gen++;
        tick.Stop();
        playing = false;
        media.Stop();
        media.Close();
        media.Source = null;
        Hide();
        var r = res;
        res = null;
        if (!exported && r != null) Rec.EditorClosed(r, work);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
