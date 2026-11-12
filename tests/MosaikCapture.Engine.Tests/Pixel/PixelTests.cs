using System.Collections;
using System.Windows.Controls;

namespace MosaikCapture.Engine.Tests.Pixel;

// Appearance: every surface is rendered off-screen and compared with References/*.png.
// Opt-in (fonts and ClearType differ between machines): run with --filter "Category=Pixel",
// CI excludes them with --filter "Category!=Pixel".
[Trait("Category", "Pixel")]
public sealed class PixelTests : IDisposable
{
    static readonly ShotOverlay.RECT WorkArea = new() { left = 0, top = 0, right = 1920, bottom = 1040 };
    readonly bool? palette = Palette.Force;

    public PixelTests() => Palette.Force = false;   // dark theme regardless of the Windows setting

    public void Dispose() => Palette.Force = palette;

    // ---------- Rendering ----------

    static void Lay(FrameworkElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        e.Arrange(new Rect(e.DesiredSize));
        e.UpdateLayout();
    }

    /// <summary>Element at double resolution on a calm gradient (shows transparency and shadows).</summary>
    static BitmapSource Draw(FrameworkElement e)
    {
        Lay(e);
        WpfThread.Pump(50);
        Lay(e);
        double w = Math.Ceiling(e.DesiredSize.Width), h = Math.Ceiling(e.DesiredSize.Height);
        var bitmap = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5A, 0x62, 0x6E), Color.FromRgb(0x9A, 0x8C, 0x7E), 30), null, new Rect(0, 0, w, h));
        bitmap.Render(background);
        bitmap.Render(e);
        bitmap.Freeze();
        return bitmap;
    }

    static BitmapSource Cutout(Visual visual, Rect area, Brush? background, double factor)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(factor, factor));
            if (background != null) dc.DrawRectangle(background, null, area);
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            dc.DrawRectangle(new VisualBrush(visual)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area
            }, null, area);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * factor), (int)Math.Ceiling(area.Height * factor), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------- Screenshot editor ----------

    static object Tool(string name) => Private.Enum(typeof(ShotEditor), "Tool", name);

    static void Stroke(ShotEditor editor, string tool, List<Point> points)
    {
        Private.Call(editor, "SetTool", Tool(tool));
        Private.Call(editor, "Down", points[0]);
        for (int i = 1; i < points.Count; i++) Private.Call(editor, "Moved", points[i]);
        Private.Call(editor, "Up");
    }

    /// <summary>Fake screenshot: dark and light text, a button, an e-mail address.</summary>
    static BitmapSource Scene()
    {
        const int w = 960, h = 620;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            string[] lines = { "Erste Zeile mit etwas Text zum Testen", "Die zweite Zeile ist wichtig für uns", "Dritte Zeile, etwas kürzer" };
            for (int i = 0; i < lines.Length; i++) dc.DrawText(Text(lines[i], 20, new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))), new Point(40, 60 + i * 34));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1E)), null, new Rect(470, 290, 460, 200));
            dc.DrawText(Text("Dunkles Feld mit heller Schrift", 16, new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))), new Point(490, 320));
            dc.DrawText(Text("Einstellungen   Konto   Hilfe", 14, new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))), new Point(490, 420));
            dc.DrawText(Text("max.mustermann@beispiel.de", 18, Brushes.Black), new Point(50, 450));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), null, new Rect(40, 200, 170, 40), 6, 6);
            dc.DrawText(Text("Absenden", 16, Brushes.White), new Point(90, 209));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public void ScreenshotEditorToolbarMenuIconsAndAllTools() => WpfThread.Run(() =>
    {
        var check = new PixelComparison();
        var editor = new ShotEditor((_, _) => { }, _ => { }, (_, _) => { });
        var root = Private.Field<Canvas>(editor, "root");
        root.Width = 3000;
        root.Height = 2000;
        Lay(root);
        var bar = Private.Field<FrameworkElement>(editor, "bar");
        Private.Call(editor, "SetTool", Tool("Arrow"));
        Lay(root);
        check.Check("editor_toolbar", Cutout(bar, new Rect(0, 0, bar.ActualWidth, bar.ActualHeight), Brushes.Gray, 3));

        // load the scene
        var shot = Scene();
        var data = new ShotData { Full = shot, Scale = 1, Vw = shot.PixelWidth, Vh = shot.PixelHeight, Cut = new Int32Rect(0, 0, shot.PixelWidth, shot.PixelHeight) };
        data.Monitor = new ShotOverlay.RECT { left = 0, top = 0, right = shot.PixelWidth, bottom = shot.PixelHeight };
        Private.SetField(editor, "data", data);
        Private.SetField(editor, "s", 1.0);
        root.Width = shot.PixelWidth;
        root.Height = shot.PixelHeight;
        var back = Private.Field<Image>(editor, "back");
        back.Source = shot;
        back.Width = shot.PixelWidth;
        back.Height = shot.PixelHeight;
        Private.SetField(editor, "sel", new Rect(0, 0, shot.PixelWidth, shot.PixelHeight));
        Private.Call(editor, "Layout");
        Lay(root);

        // menu of the shapes group
        Private.Call(editor, "SetTool", Tool("Box"));
        Private.Call(editor, "OpenMenu", Private.Field<IList>(editor, "groups")[2]);
        Lay(root);
        var menu = Private.Field<FrameworkElement>(editor, "menu");
        Rect area = Rect.Union(new Rect(Canvas.GetLeft(bar), Canvas.GetTop(bar), bar.ActualWidth, bar.ActualHeight),
                               new Rect(Canvas.GetLeft(menu), Canvas.GetTop(menu), menu.ActualWidth, menu.ActualHeight));
        area.Inflate(16, 16);
        check.Check("editor_menu", Cutout(root, area, null, 2));
        Private.Call(editor, "CloseMenu");

        // tool icons, large
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x2E)) };
        foreach (var name in new[] { "Move", "Arrow", "Box", "Ellipse", "Line", "Pen", "Marker", "Spot", "Text", "Label", "Number", "Pixel", "Blur" })
            icons.Children.Add(new Grid { Width = 36, Height = 36, Children = { (UIElement)Private.Call(editor, "ToolIcon", Tool(name), new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF5)))! } });
        Lay(icons);
        check.Check("editor_icons", Cutout(icons, new Rect(icons.DesiredSize), null, 4));

        // every tool in the scene
        Private.Call(editor, "SetColor", 0);
        Private.Call(editor, "SetWidth", 2);
        Stroke(editor, "Marker", Strokes.Line(new Point(118, 106), new Point(262, 110), 30));   // highlighter, slanted and too short
        Private.Call(editor, "SetColor", 2);
        Stroke(editor, "Marker", Strokes.Line(new Point(512, 331), new Point(640, 327), 30));   // light text on dark
        Private.Call(editor, "SetColor", 0);
        Stroke(editor, "Spot", Strokes.Line(new Point(470, 390), new Point(700, 470), 5));
        Stroke(editor, "Blur", Strokes.Line(new Point(40, 440), new Point(250, 480), 5));
        Private.Call(editor, "SetTool", Tool("Label"));
        Private.Call(editor, "Down", new Point(330, 200));
        Private.Field<TextBox>(editor, "typing").Text = "Hier klicken";
        Lay(root);
        Private.Call(editor, "Commit");
        Private.Call(editor, "SetColor", 5);
        Private.Call(editor, "SetWidth", 1);
        Private.Call(editor, "Down", new Point(330, 250));
        Private.Field<TextBox>(editor, "typing").Text = "Hell";
        Lay(root);
        Private.Call(editor, "Commit");
        Private.Call(editor, "SetColor", 4);
        Stroke(editor, "Pen", Strokes.Wobble(Strokes.Ellipse(new Point(820, 110), 50, 40, 0.1), 3, 7));   // freehand, smoothed
        var held = Strokes.Wobble(Strokes.Ellipse(new Point(820, 250), 45, 45, 0.15), 2.5, 11);          // held still: becomes a circle
        Private.Call(editor, "SetTool", Tool("Pen"));
        Private.Call(editor, "Down", held[0]);
        for (int i = 1; i < held.Count; i++) Private.Call(editor, "Moved", held[i]);
        Private.Call(editor, "Held");
        Private.Call(editor, "Moved", new Point(held[held.Count - 1].X + 12, held[held.Count - 1].Y));
        Private.Call(editor, "Up");
        Private.Call(editor, "SetColor", 3);
        Stroke(editor, "Ellipse", Strokes.Line(new Point(40, 520), new Point(180, 580), 5));
        Stroke(editor, "Line", Strokes.Line(new Point(220, 550), new Point(400, 520), 5));
        Stroke(editor, "Arrow", Strokes.Line(new Point(420, 560), new Point(470, 510), 5));
        Lay(root);
        check.Check("editor_scene", (BitmapSource)Private.Call(editor, "Render")!);
        check.AssertAllMatch();
    });

    // ---------- Screen recording ----------

    /// <summary>Thumbnail without ffmpeg: calm color areas at 16:9.</summary>
    static BitmapSource Thumbnail()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x55), Color.FromRgb(0xD9, 0x77, 0x57), 20), null, new Rect(0, 0, 960, 540));
            dc.DrawRectangle(Brushes.White, null, new Rect(80, 80, 400, 260));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), null, new Point(700, 300), 120, 120);
        }
        var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Stops the card's progress bar so the image does not depend on timing.</summary>
    static void FreezeBar(RecCard card, double width)
    {
        var scale = Private.Field<ScaleTransform>(card, "barScale");
        var move = Private.Field<TranslateTransform>(card, "barMove");
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        move.BeginAnimation(TranslateTransform.XProperty, null);
        scale.ScaleX = width;
        move.X = 0;
    }

    [Fact]
    public void RecordingCardDoneSavingAndFailed() => WpfThread.Run(() =>
    {
        var check = new PixelComparison();
        var done = new RecResult
        {
            Path = @"C:\Videos\Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4",
            Duration = 42.4,
            Width = 1920,
            Height = 1080,
            Audio = true,
            Bytes = 18350080,
            Thumb = Thumbnail()
        };
        var card = new RecCard { Offscreen = true };
        card.Present(done, "Aufnahme gespeichert", WorkArea);
        FreezeBar(card, 1);
        check.Check("recording_card", Draw((FrameworkElement)card.Content));

        card = new RecCard { Offscreen = true };
        card.Saving(83, WorkArea);
        FreezeBar(card, 0.3);
        check.Check("recording_card_saving", Draw((FrameworkElement)card.Content));

        card = new RecCard { Offscreen = true };
        card.Problem("Aufnahme fehlgeschlagen", "Dieser Monitor ist für die Aufnahme nicht erreichbar", WorkArea);
        FreezeBar(card, 1);
        check.Check("recording_card_error", Draw((FrameworkElement)card.Content));
        check.AssertAllMatch();
    });

    [Fact]
    public void RecordingBarNormalMutedMicrophoneAndDiscardPrompt() => WpfThread.Run(() =>
    {
        var check = new PixelComparison();
        bool mic = Rec.Mic, sound = Rec.Sound, clicks = Rec.Clicks;
        try
        {
            var bar = new RecBar();
            Rec.Mic = true;
            Rec.Sound = true;
            Rec.Clicks = true;
            bar.Refresh();
            Private.Field<TextBlock>(bar, "time").Text = "0:42";
            check.Check("recording_bar", Draw((FrameworkElement)bar.Content));

            Rec.Mic = false;
            bar.Refresh();
            Lay((FrameworkElement)bar.Content);
            Private.Call(bar, "Hover", Private.Field<Border>(bar, "soundBtn"));
            check.Check("recording_bar_caption", Draw((FrameworkElement)bar.Content));

            Private.Call(bar, "Hover", new object?[] { null });
            Private.SetField(bar, "confirm", true);
            bar.Refresh();
            Lay((FrameworkElement)bar.Content);
            Private.Call(bar, "Hover", Private.Field<Border>(bar, "trashBtn"));
            check.Check("recording_bar_discard", Draw((FrameworkElement)bar.Content));
        }
        finally
        {
            Rec.Mic = mic;
            Rec.Sound = sound;
            Rec.Clicks = clicks;
        }
        check.AssertAllMatch();
    });

    [Fact]
    public void CountdownAndFrameAroundTheArea() => WpfThread.Run(() =>
    {
        var check = new PixelComparison();
        var countdown = new RecCountdown();
        Private.Call(countdown, "Show", 2, 0.6);
        WpfThread.Pump(400);   // wait for the number to fade in
        check.Check("recording_countdown", Draw((FrameworkElement)countdown.Content));

        var frame = new RecFrame();
        frame.Build(new Int32Rect(40, 40, 600, 340), new ShotOverlay.RECT { left = 0, top = 0, right = 1920, bottom = 1080 }, 1);
        check.Check("recording_frame", Draw((FrameworkElement)frame.Content));
        check.AssertAllMatch();
    });

    [Fact]
    [Trait("Category", "Ffmpeg")]
    public void TrimWindowWithFilmStripSelectionAndPlayhead()
    {
        Ffmpeg.SkipIfMissing();
        using var work = new TempDirectory();
        string video = work.File("probe.mp4");
        Ffmpeg.TestVideo(video, 6);

        WpfThread.Run(() =>
        {
            var check = new PixelComparison();
            var editor = new RecEditor { Offscreen = true };
            editor.Open(RecResult.Load(video), WorkArea);
            Assert.True(WpfThread.PumpUntil(() => Private.Field<Image>(editor, "strip").Source != null, 20000), "film strip did not arrive");
            Private.SetField(editor, "a", 1.2);
            Private.SetField(editor, "b", 4.1);
            Private.SetField(editor, "pos", 3.4);
            Private.Call(editor, "Draw");
            check.Check("recording_trim", Draw((FrameworkElement)editor.Content));
            check.AssertAllMatch();
        });
    }
}
