using System.Windows.Controls;

namespace MosaikCapture.Engine.Tests.Screenshot;

// What the editor draws on top of the screenshot while working: the frame of an effect area and notices.
public sealed class EditorFeedbackTests : IDisposable
{
    const int Width = 400, Height = 300;
    readonly bool? palette = Palette.Force;

    public EditorFeedbackTests() => Palette.Force = false;   // dark theme: its text color is light

    public void Dispose() => Palette.Force = palette;

    static BitmapSource Plain(Color color)
    {
        var pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        image.Freeze();
        return image;
    }

    static ScreenshotEditor Editor(BitmapSource shot)
    {
        var editor = new ScreenshotEditor((_, _) => { }, _ => { }, (_, _) => { });
        editor.Load(new ScreenCapture
        {
            FullScreen = shot,
            Image = shot,
            Scale = 1,
            VirtualWidth = Width,
            VirtualHeight = Height,
            Crop = new Int32Rect(0, 0, Width, Height),
            Monitor = new RECT { left = 0, top = 0, right = Width, bottom = Height },
        });
        Lay(editor.Surface);
        return editor;
    }

    static void Lay(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    // Brightness of the pixels on and just inside the frame's left edge (x = 100, between y = 120 and 180).
    static List<double> FrameEdge(Canvas surface)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        var values = new List<double>();
        for (int y = 120; y < 180; y++)
            for (int x = 100; x <= 103; x++)   // the line and the inside: the spotlight dims the outside anyway
            {
                int i = (y * Width + x) * 4;
                values.Add((pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0);
            }
        return values;
    }

    [Theory]
    [InlineData("Blur")]
    [InlineData("Pixel")]
    [InlineData("Spot")]
    public void TheFrameOfAnEffectAreaShowsOnWhiteAndOnBlack(string tool) => WpfThread.Run(() =>
    {
        foreach (var (background, mustContain) in new[] { (Colors.White, "dark"), (Colors.Black, "light") })
        {
            var editor = Editor(Plain(background));
            editor.SetTool((ScreenshotEditor.Tool)Enum.Parse(typeof(ScreenshotEditor.Tool), tool));
            editor.PointerDown(new Point(100, 100));
            editor.PointerMoved(new Point(300, 220));
            Lay(editor.Surface);

            var edge = FrameEdge(editor.Surface);

            if (mustContain == "dark") Assert.True(edge.Min() < 128, "the frame is invisible on white content (darkest " + edge.Min() + ")");
            else Assert.True(edge.Max() > 128, "the frame is invisible on black content (brightest " + edge.Max() + ")");
            editor.PointerUp();
        }
    });

    [Fact]
    public void TheFirstNoticeAfterLoadIsMeasuredWithItsText() => WpfThread.Run(() =>
    {
        var editor = Editor(Plain(Colors.White));
        var notice = editor.Notice;
        var toolbar = editor.Toolbar;

        editor.Notify("Gespeichert: Mosaiks-Screenshot 2026-09-26 14-03-12.png");

        Assert.True(notice.DesiredSize.Width > 250, "measured without its text: " + notice.DesiredSize.Width);
        double noticeCenter = Canvas.GetLeft(notice) + notice.DesiredSize.Width / 2, toolbarCenter = Canvas.GetLeft(toolbar) + toolbar.ActualWidth / 2;
        Assert.Equal(toolbarCenter, noticeCenter, 1);

        editor.Notify("Kopiert");   // shorter: must shrink, not keep the earlier size
        Assert.True(notice.DesiredSize.Width < 150, "kept the size of the earlier text: " + notice.DesiredSize.Width);
    });
}
