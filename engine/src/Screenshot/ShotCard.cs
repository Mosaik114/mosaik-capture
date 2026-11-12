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

// Karte unten rechts nach dem Aufnehmen: Vorschau, Dateiname, Bearbeiten / Ordner / Loeschen.
// Vorschau anklicken = Bild oeffnen, Vorschau ziehen = Datei in eine andere App ablegen.
// Nimmt keinen Fokus, verschwindet nach 5 Sekunden (solange die Maus drauf ist, nicht).
public class ShotCard : Window
{
    const double Inner = 314, MaxThumb = 176, Life = 5000;
    readonly IntPtr hwnd;
    readonly Border card, thumbBox, sizeChip, okChip;
    readonly Image thumb;
    readonly TextBlock title, sub, sizeText, okGlyph;
    readonly System.Windows.Controls.Primitives.UniformGrid buttons;
    readonly Border bar;
    readonly ScaleTransform left = new ScaleTransform(1, 1);
    readonly TranslateTransform shift = new TranslateTransform();
    readonly Action onEdit, onPin, onGone;
    string path;
    bool saved;
    int gen;
    Point down;
    bool pressing;

    BitmapSource shown;

    public ShotCard(Action edit, Action pin, Action gone)
    {
        onEdit = edit;
        onPin = pin;
        onGone = gone;
        Title = "Mosaiks-Screenshot Karte";
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
        var ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        var st = new StackPanel();
        thumb = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sizeText = new TextBlock { FontSize = 11, FontFamily = ui };
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sizeChip = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = sizeText,
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x26, 0x26, 0x24)), IsHitTestVisible = false
        };
        var tg = new Grid();
        tg.Children.Add(thumb);
        tg.Children.Add(sizeChip);
        thumbBox = new Border { CornerRadius = new CornerRadius(8), Width = Inner, Child = tg };
        thumbBox.SetResourceReference(Border.BackgroundProperty, "Hover");
        st.Children.Add(thumbBox);

        okGlyph = new TextBlock { Text = "\uE73E", FontSize = 11, FontFamily = icons, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        okGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        okChip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = okGlyph };
        okChip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        title = new TextBlock { FontFamily = new FontFamily("Georgia"), FontSize = 17 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sub = new TextBlock { FontFamily = ui, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(sub);
        var head = new DockPanel { Margin = new Thickness(2, 12, 2, 0) };
        head.Children.Add(okChip);
        head.Children.Add(texts);
        st.Children.Add(head);

        buttons = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("\uE70F", "Bearbeiten", delegate { if (saved) onEdit(); }));
        buttons.Children.Add(Button("\uE8B7", "Ordner", delegate { if (saved) Program.Reveal(path); }));
        buttons.Children.Add(Button("\uE74D", "Löschen", delegate { Delete(); }));
        buttons.Children.Insert(1, Button("\uE718", "Anpinnen", delegate { onPin(); FadeOut(); }));
        buttons.Children.Insert(2, Button("\uE8AC", "Text", ReadText));
        st.Children.Add(buttons);

        var track = new Grid { Height = 3, Margin = new Thickness(3, 12, 3, 0) };
        var rail = new Border { CornerRadius = new CornerRadius(1.5) };
        rail.SetResourceReference(Border.BackgroundProperty, "Line");
        bar = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = left };
        bar.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(rail);
        track.Children.Add(bar);
        st.Children.Add(track);

        card = new Border
        {
            Width = Inner + 26, Margin = new Thickness(24), Padding = new Thickness(12), CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1), Child = st, Opacity = 0, RenderTransform = shift,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35, Color = Colors.Black }
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        Content = card;

        // Solange die Maus auf der Karte ist, bleibt sie stehen
        card.MouseEnter += delegate { left.BeginAnimation(ScaleTransform.ScaleXProperty, null); left.ScaleX = 1; };
        card.MouseLeave += delegate { if (IsVisible && !pressing) Countdown(); };
        card.MouseRightButtonUp += delegate { FadeOut(); };

        thumb.MouseLeftButtonDown += (o, e) => { down = e.GetPosition(this); pressing = true; thumb.CaptureMouse(); e.Handled = true; };
        thumb.MouseMove += (o, e) =>
        {
            if (!pressing || !saved) return;
            Vector v = e.GetPosition(this) - down;
            if (Math.Abs(v.X) < 6 && Math.Abs(v.Y) < 6) return;
            pressing = false;
            thumb.ReleaseMouseCapture();
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(thumb, data, DragDropEffects.Copy);   // in Chat, Mail, Explorer ... ziehen
            if (!card.IsMouseOver) Countdown();
        };
        thumb.MouseLeftButtonUp += (o, e) =>
        {
            if (!pressing) return;
            pressing = false;
            thumb.ReleaseMouseCapture();
            if (saved) { Program.Open(path); FadeOut(); }
        };

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000080);   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    }

    Border Button(string glyph, string label, Action run)
    {
        var v = new StackPanel();
        var g = new TextBlock { Text = glyph, FontSize = 15, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var t = new TextBlock { Text = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        v.Children.Add(g);
        v.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0, 7, 0, 6), Child = v, Cursor = Cursors.Hand };
        b.SetResourceReference(Border.BackgroundProperty, "Hover");
        b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Sel"); t.SetResourceReference(TextBlock.ForegroundProperty, "Text"); };
        b.MouseLeave += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); };
        b.MouseLeftButtonUp += delegate { run(); };
        return b;
    }

    public void Present(ShotData d, string heading)
    {
        BitmapSource img = d.Image;
        string file = d.Path;
        ShotOverlay.RECT work = d.Work;
        Palette.Apply(Resources);
        path = file;
        saved = false;
        gen++;
        // Vorschau im Seitenverhaeltnis, abgerundet; hohe Bilder werden schmaler statt abgeschnitten
        double w = Inner, h = Inner * img.PixelHeight / img.PixelWidth;
        if (h > MaxThumb) { w = w * MaxThumb / h; h = MaxThumb; }
        thumb.Source = img;
        shown = img;
        thumb.Width = w;
        thumb.Height = h;
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 8, 8);
        thumbBox.Height = Math.Max(h, 90);
        sizeText.Text = img.PixelWidth + " × " + img.PixelHeight;
        title.Text = heading;
        sub.Text = System.IO.Path.GetFileName(file);
        okGlyph.Text = "\uE73E";
        buttons.Visibility = Visibility.Visible;

        bool fresh = !IsVisible;
        if (fresh)
        {
            Left = -10000;   // erst ausmessen, dann an den Rand setzen
            Show();
        }
        UpdateLayout();
        var src = HwndSource.FromHwnd(hwnd);
        double sc = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
        int pw = (int)Math.Round(ActualWidth * sc), ph = (int)Math.Round(ActualHeight * sc);
        SetWindowPos(hwnd, (IntPtr)(-1), work.right - pw, work.bottom - ph, 0, 0, 0x11);   // TOPMOST, SWP_NOSIZE|NOACTIVATE

        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, dur) { EasingFunction = ease };
        var rise = new DoubleAnimation(0, dur) { EasingFunction = ease };
        if (fresh) { fade.From = 0; rise.From = 12; }
        card.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
        if (!card.IsMouseOver) Countdown();
    }

    public void Saved() { saved = true; }

    // Text im Bild erkennen und kopieren; das Ergebnis steht in der Karte
    async void ReadText()
    {
        var img = shown;
        if (img == null) return;
        int g = gen;
        title.Text = "Text wird gelesen …";
        string text = await ShotText.Read(img);
        if (g != gen) return;
        if (string.IsNullOrEmpty(text)) { title.Text = "Kein Text gefunden"; return; }
        ShotText.ToClipboard(text);
        title.Text = "Text kopiert";
        sub.Text = ShotText.Summary(text);
    }

    // Fuer den Editor sofort weg, ohne die Aufnahme freizugeben
    public void HideNow()
    {
        gen++;
        left.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hide();
    }

    public void Failed()
    {
        title.Text = "Nicht gespeichert";
        sub.Text = "Ordner nicht beschreibbar: " + Shot.Folder;
        buttons.Visibility = Visibility.Collapsed;
    }

    void Countdown()
    {
        int g = gen;
        var run = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(Life)));
        run.Completed += delegate { if (g == gen && !card.IsMouseOver && left.ScaleX <= 0.001) FadeOut(); };
        left.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }

    // In den Papierkorb, damit es rueckgaengig zu machen ist
    void Delete()
    {
        if (!saved) return;
        var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = path + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 };
        if (SHFileOperation(ref op) != 0) return;
        saved = false;
        title.Text = "Gelöscht";
        sub.Text = "Liegt im Papierkorb";
        okGlyph.Text = "\uE74D";
        buttons.Visibility = Visibility.Collapsed;
        gen++;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += delegate { t.Stop(); FadeOut(); };
        t.Start();
    }

    void FadeOut()
    {
        int g = ++gen;
        left.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += delegate
        {
            if (g != gen) return;
            Hide();
            thumb.Source = null;
            onGone();
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
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
