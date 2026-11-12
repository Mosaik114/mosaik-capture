using System.Reflection;

namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Compares rendered UI against the reference images in References/. Mismatching images are written
/// to "pixel-diffs" next to the test assembly together with a diff (differences in red).
/// Set MOSAIK_UPDATE_REFERENCES=1 to rewrite the references after an intended visual change
/// (and review the new images before committing them).
/// </summary>
/// <remarks>
/// The references were rendered on Windows 11. Text is the fragile part: the UI uses Segoe UI Variable,
/// Segoe Fluent Icons and Georgia, and other Windows builds ship other versions of these fonts (different
/// hinting moves glyph edges by a pixel) or lack them entirely (Windows Server, and with it the GitHub
/// windows-latest image, has no Segoe UI Variable / Segoe Fluent Icons, so WPF falls back to other fonts).
/// Therefore:
/// <list type="bullet">
/// <item>Missing fonts skip the pixel tests with the font names as the reason (<see cref="RequireFonts"/>).</item>
/// <item>The default comparison is strict (2 per channel, at most 0.01 % of the pixels).</item>
/// <item>MOSAIK_PIXEL_TOLERANCE=lenient compares slightly blurred images with a wider tolerance, which absorbs
/// re-hinted glyph edges but still catches moved elements, other colors or other text.</item>
/// </list>
/// </remarks>
internal sealed class PixelComparison
{
    /// <summary>Fonts the reference images were rendered with.</summary>
    static readonly string[] RequiredFonts = { "Segoe UI", "Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe Fluent Icons", "Georgia" };

    static string References => Path.Combine(AppContext.BaseDirectory, "References");
    static string Diffs => Path.Combine(AppContext.BaseDirectory, "pixel-diffs");
    static bool Update => Environment.GetEnvironmentVariable("MOSAIK_UPDATE_REFERENCES") == "1";
    static bool Lenient => string.Equals(Environment.GetEnvironmentVariable("MOSAIK_PIXEL_TOLERANCE"), "lenient", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per channel tolerance: tiny anti-aliasing differences (strict) or re-hinted glyph edges after blurring (lenient).</summary>
    static int Tolerance => Lenient ? 24 : 2;

    /// <summary>
    /// Share of pixels that may differ by more than <see cref="Tolerance"/>: font updates re-rasterize a
    /// few glyph edges, while a real visual change (moved element, other text or color) affects far more.
    /// </summary>
    static double AllowedShare => Lenient ? 0.002 : 0.0001;

    /// <summary>Skips the calling test when a font of the references is not installed (see the remarks).</summary>
    public static void RequireFonts()
    {
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        var missing = RequiredFonts.Where(f => !installed.Contains(f)).ToList();
        Assert.SkipWhen(missing.Count > 0, "fonts of the reference images are not installed: " + string.Join(", ", missing));
    }

    readonly List<string> failures = new();

    public void Check(string name, BitmapSource actual)
    {
        actual = AsPng(actual);   // compare what would be saved (PNG rounds semi-transparent edge pixels)
        if (Update)
        {
            string source = typeof(PixelComparison).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "ReferenceSourceDirectory").Value!;
            Save(actual, Path.Combine(source, name + ".png"));
            return;
        }
        string file = Path.Combine(References, name + ".png");
        if (!File.Exists(file)) { Keep(name, actual, null); failures.Add(name + ": reference image is missing"); return; }

        var expected = Pixels(Load(file));
        var current = Pixels(actual);
        var compareExpected = Lenient ? Blur(expected) : expected;
        var compareCurrent = Lenient ? Blur(current) : current;
        if (expected.Width != current.Width || expected.Height != current.Height)
        {
            Keep(name, actual, null);
            failures.Add(name + ": size " + current.Width + " x " + current.Height + " instead of " + expected.Width + " x " + expected.Height);
            return;
        }
        int tolerance = Tolerance, different = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        var diff = new byte[expected.Data.Length];
        for (int i = 0; i < expected.Data.Length; i += 4)
        {
            bool same = true;
            for (int c = 0; c < 4; c++) same &= Math.Abs(compareExpected.Data[i + c] - compareCurrent.Data[i + c]) <= tolerance;
            byte gray = (byte)(255 - (255 - (expected.Data[i] + expected.Data[i + 1] + expected.Data[i + 2]) / 3) / 4);
            diff[i] = same ? gray : (byte)0;
            diff[i + 1] = same ? gray : (byte)0;
            diff[i + 2] = same ? gray : (byte)255;
            diff[i + 3] = 255;
            if (same) continue;
            different++;
            int x = i / 4 % expected.Width, y = i / 4 / expected.Width;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        if (different <= Math.Max(4, expected.Width * expected.Height * AllowedShare)) return;
        Keep(name, actual, BitmapSource.Create(expected.Width, expected.Height, 96, 96, PixelFormats.Bgra32, null, diff, expected.Width * 4));
        failures.Add(name + ": " + different + " pixels differ, within " + x0 + "," + y0 + " to " + x1 + "," + y1);
    }

    public void AssertAllMatch()
    {
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures) + "\n(images in " + Diffs + ")");
    }

    static void Keep(string name, BitmapSource actual, BitmapSource? diff)
    {
        Directory.CreateDirectory(Diffs);
        Save(actual, Path.Combine(Diffs, name + ".png"));
        if (diff != null) Save(diff, Path.Combine(Diffs, name + ".diff.png"));
    }

    sealed class Raw
    {
        public int Width, Height;
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>3 x 3 box blur per channel: a glyph edge moved by one pixel then differs only a little.</summary>
    static Raw Blur(Raw image)
    {
        var blurred = new Raw { Width = image.Width, Height = image.Height, Data = new byte[image.Data.Length] };
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= image.Width || ny >= image.Height) continue;
                            sum += image.Data[(ny * image.Width + nx) * 4 + c];
                            count++;
                        }
                    blurred.Data[(y * image.Width + x) * 4 + c] = (byte)(sum / count);
                }
        return blurred;
    }

    static Raw Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var raw = new Raw { Width = converted.PixelWidth, Height = converted.PixelHeight, Data = new byte[converted.PixelWidth * converted.PixelHeight * 4] };
        converted.CopyPixels(raw.Data, raw.Width * 4, 0);
        return raw;
    }

    static BitmapSource Load(string file)
    {
        using var stream = File.OpenRead(file);
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    static BitmapSource AsPng(BitmapSource bitmap)
    {
        var stream = new MemoryStream(Screenshots.Encode(bitmap, false));
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    static void Save(BitmapSource bitmap, string file) => File.WriteAllBytes(file, Screenshots.Encode(bitmap, false));
}
