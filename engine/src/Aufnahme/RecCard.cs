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

// ---------- Karte nach der Aufnahme (wie beim Screenshot) ----------

public class RecCard : Window
{
    const double Inner = 314, MaxThumb = 176, Life = 8000;
    readonly IntPtr hwnd;
    readonly Border card, thumbBox, lenChip, playChip, okChip, bar;
    readonly Image thumb;
    readonly TextBlock title, sub, lenText, okGlyph, waitGlyph;
    readonly System.Windows.Controls.Primitives.UniformGrid buttons;
    readonly ScaleTransform barScale = new ScaleTransform(1, 1);
    readonly TranslateTransform barMove = new TranslateTransform();
    readonly TranslateTransform shift = new TranslateTransform();
    RecResult res;
    ShotOverlay.RECT work;
    bool ready, pressing, counting;
    int gen;
    Point down;

    public RecCard()
    {
        RecUi.Setup(this, "Mosaiks-Aufnahme Karte");
        SizeToContent = SizeToContent.WidthAndHeight;

        var st = new StackPanel();
        thumb = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var playGlyph = RecUi.Glyph("\uF5B0", 16);
        playGlyph.Foreground = RecUi.Light;
        playGlyph.Margin = new Thickness(3, 0, 0, 0);   // Dreieck optisch mittig
        playChip = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Child = playGlyph, IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x13)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        lenText = new TextBlock { FontSize = 11, FontFamily = RecUi.Ui, Foreground = RecUi.Light };
        Typography.SetNumeralAlignment(lenText, FontNumeralAlignment.Tabular);
        lenChip = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = lenText,
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x26, 0x26, 0x24)), IsHitTestVisible = false
        };
        waitGlyph = RecUi.Glyph("\uE7C8", 26);
        waitGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var tg = new Grid();
        tg.Children.Add(waitGlyph);
        tg.Children.Add(thumb);
        tg.Children.Add(playChip);
        tg.Children.Add(lenChip);
        thumbBox = new Border { CornerRadius = new CornerRadius(8), Width = Inner, Child = tg };
        thumbBox.SetResourceReference(Border.BackgroundProperty, "Hover");
        st.Children.Add(thumbBox);

        okGlyph = RecUi.Glyph("\uE73E", 11);
        okGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        okChip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = okGlyph };
        okChip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        title = new TextBlock { FontFamily = new FontFamily("Georgia"), FontSize = 17 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sub = new TextBlock { FontFamily = RecUi.Ui, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(sub);
        var head = new DockPanel { Margin = new Thickness(2, 12, 2, 0) };
        head.Children.Add(okChip);
        head.Children.Add(texts);
        st.Children.Add(head);

        buttons = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        st.Children.Add(buttons);

        var track = new Grid { Height = 3, Margin = new Thickness(3, 12, 3, 0), ClipToBounds = true };
        var rail = new Border { CornerRadius = new CornerRadius(1.5) };
        rail.SetResourceReference(Border.BackgroundProperty, "Line");
        var tf = new TransformGroup();
        tf.Children.Add(barScale);
        tf.Children.Add(barMove);
        bar = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = tf };
        bar.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(rail);
        track.Children.Add(bar);
        st.Children.Add(track);

        card = new Border
        {
            Width = Inner + 26, Margin = new Thickness(24), Padding = new Thickness(12), CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1), Child = st, Opacity = 0, RenderTransform = shift, Effect = RecUi.Shadow(28, 8, 0.35)
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        Content = card;

        // Solange die Maus auf der Karte ist, bleibt sie stehen
        card.MouseEnter += delegate { if (counting) { barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); barScale.ScaleX = 1; } };
        card.MouseLeave += delegate { if (IsVisible && !pressing && counting) Countdown(); };
        card.MouseRightButtonUp += delegate { if (ready || counting) FadeOut(); };

        thumb.MouseLeftButtonDown += (o, e) => { down = e.GetPosition(this); pressing = true; thumb.CaptureMouse(); e.Handled = true; };
        thumb.MouseMove += (o, e) =>
        {
            if (!pressing || !ready) return;
            Vector v = e.GetPosition(this) - down;
            if (Math.Abs(v.X) < 6 && Math.Abs(v.Y) < 6) return;
            pressing = false;
            thumb.ReleaseMouseCapture();
            DragDrop.DoDragDrop(thumb, new DataObject(DataFormats.FileDrop, new[] { res.Path }), DragDropEffects.Copy);   // in Chat, Mail, Explorer ... ziehen
            if (!card.IsMouseOver && counting) Countdown();
        };
        thumb.MouseLeftButtonUp += (o, e) =>
        {
            if (!pressing) return;
            pressing = false;
            thumb.ReleaseMouseCapture();
            if (ready) { Program.Open(res.Path); FadeOut(); }
        };

        hwnd = RecUi.Handle(this, RecUi.NoActivate | RecUi.Tool);
    }

    Border Button(string glyph, string label, Action run)
    {
        var v = new StackPanel();
        var g = RecUi.Glyph(glyph, 15);
        g.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var t = new TextBlock { Text = label, FontSize = 11, FontFamily = RecUi.Ui, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        v.Children.Add(g);
        v.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0, 7, 0, 6), Child = v, Cursor = Cursors.Hand };
        b.SetResourceReference(Border.BackgroundProperty, "Hover");
        b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Sel"); t.SetResourceReference(TextBlock.ForegroundProperty, "Text"); };
        b.MouseLeave += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); };
        b.MouseLeftButtonUp += delegate { if (ready) run(); };
        return b;
    }

    // Waehrend ffmpeg zusammensetzt: Platzhalter und laufender Balken
    public void Saving(double seconds, ShotOverlay.RECT wk)
    {
        work = wk;
        res = null;
        ready = false;
        gen++;
        Palette.Apply(Resources);
        thumb.Source = null;
        thumb.Visibility = playChip.Visibility = lenChip.Visibility = Visibility.Collapsed;
        waitGlyph.Visibility = Visibility.Visible;
        thumbBox.Height = 90;
        title.Text = "Wird gespeichert …";
        sub.Text = RecUi.Clock(seconds) + " aufgenommen";
        okGlyph.Text = "\uE73E";
        okChip.Visibility = Visibility.Collapsed;
        buttons.Visibility = Visibility.Collapsed;
        Spin();
        Place();
    }

    public void Present(RecResult r, string heading, ShotOverlay.RECT wk)
    {
        work = wk;
        res = r;
        ready = true;
        gen++;
        Palette.Apply(Resources);
        BitmapSource img = r.Thumb;
        double w = Inner, h = 90;
        if (img != null && img.PixelWidth > 0)
        {
            h = Inner * img.PixelHeight / img.PixelWidth;
            if (h > MaxThumb) { w = w * MaxThumb / h; h = MaxThumb; }
        }
        thumb.Source = img;
        thumb.Width = w;
        thumb.Height = h;
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 8, 8);
        thumb.Visibility = lenChip.Visibility = playChip.Visibility = Visibility.Visible;
        waitGlyph.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        thumbBox.Height = Math.Max(h, 90);
        lenText.Text = (r.Gif ? "GIF · " : "") + RecUi.Clock(Math.Round(r.Duration));
        title.Text = heading;
        sub.Text = Path.GetFileName(r.Path) + " · " + RecUi.Bytes(r.Bytes);
        okGlyph.Text = "\uE73E";
        okChip.Visibility = Visibility.Visible;

        buttons.Children.Clear();
        if (!r.Gif)
        {
            buttons.Children.Add(Button("\uE8C6", "Kürzen", delegate { Rec.Trim(res, work); }));
            buttons.Children.Add(Button("\uF4A9", "GIF", delegate { Rec.MakeGif(res, work); }));
        }
        buttons.Children.Add(Button("\uE8B7", "Ordner", delegate { Program.Reveal(res.Path); }));
        buttons.Children.Add(Button("\uE74D", "Löschen", Delete));
        buttons.Visibility = Visibility.Visible;
        Place();
        if (!card.IsMouseOver) Countdown(); else counting = true;
    }

    // GIF wird erstellt: Buttons weg, Balken zeigt den Fortschritt
    public void Busy(string heading)
    {
        ready = false;
        gen++;
        title.Text = heading;
        buttons.Visibility = Visibility.Collapsed;
        counting = false;
        barMove.BeginAnimation(TranslateTransform.XProperty, null);
        barMove.X = 0;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        barScale.ScaleX = 0;
    }

    public void Progress(double p)
    {
        if (!ready) barScale.ScaleX = p;
    }

    public void Problem(string heading, string reason, ShotOverlay.RECT wk)
    {
        work = wk;
        res = null;
        ready = false;
        gen++;
        Palette.Apply(Resources);
        thumb.Source = null;
        thumb.Visibility = playChip.Visibility = lenChip.Visibility = Visibility.Collapsed;
        waitGlyph.Visibility = Visibility.Visible;
        thumbBox.Height = 90;
        title.Text = heading;
        sub.Text = reason;
        okGlyph.Text = "\uE7BA";
        okChip.Visibility = Visibility.Visible;
        buttons.Visibility = Visibility.Collapsed;
        Place();
        Countdown();
    }

    public void HideNow()
    {
        gen++;
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hide();
    }

    // Unten rechts auf dem Arbeitsbereich; beim ersten Zeigen weich hereinschieben
    internal bool Offscreen { get; set; }   // nur fuer Pruefbilder: Inhalt aufbauen, aber nichts zeigen

    void Place()
    {
        if (Offscreen) { card.Opacity = 1; return; }
        bool fresh = !IsVisible;
        if (fresh)
        {
            Left = -10000;
            Show();
        }
        UpdateLayout();
        double sc = RecUi.Scale(hwnd);
        int pw = (int)Math.Round(ActualWidth * sc), ph = (int)Math.Round(ActualHeight * sc);
        RecUi.Move(hwnd, work.right - pw, work.bottom - ph);
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, dur) { EasingFunction = ease };
        var rise = new DoubleAnimation(0, dur) { EasingFunction = ease };
        if (fresh) { fade.From = 0; rise.From = 12; }
        card.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    // Laufender Balken ohne bekanntes Ende
    void Spin()
    {
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        barScale.ScaleX = 0.3;
        double w = Inner - 6;
        barMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.3 * w, w, new Duration(TimeSpan.FromMilliseconds(1100)))
        {
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    void Countdown()
    {
        counting = true;
        int g = gen;
        barMove.BeginAnimation(TranslateTransform.XProperty, null);
        barMove.X = 0;
        var run = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(Life)));
        run.Completed += delegate { if (g == gen && !card.IsMouseOver && barScale.ScaleX <= 0.001) FadeOut(); };
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }

    // In den Papierkorb, damit es rueckgaengig zu machen ist
    void Delete()
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = res.Path + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 };
        if (SHFileOperation(ref op) != 0) return;
        ready = false;
        title.Text = "Gelöscht";
        sub.Text = "Liegt im Papierkorb";
        okGlyph.Text = "\uE74D";
        buttons.Visibility = Visibility.Collapsed;
        int g = ++gen;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += delegate { t.Stop(); if (g == gen) FadeOut(); };
        t.Start();
    }

    void FadeOut()
    {
        int g = ++gen;
        counting = false;
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += delegate
        {
            if (g != gen) return;
            Hide();
            thumb.Source = null;
            res = null;
        };
        card.BeginAnimation(OpacityProperty, fade);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom, pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
