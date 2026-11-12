using System.Reflection;

namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Compares rendered UI against the reference images in References/. Mismatching images are written
/// to "pixel-diffs" next to the test assembly together with a diff (differences in red).
/// Set MOSAIK_UPDATE_REFERENCES=1 to rewrite the references after an intended visual change
/// (and review the new images before committing them).
/// </summary>
internal sealed class PixelComparison
{
    /// <summary>Per channel tolerance for tiny anti-aliasing differences.</summary>
    const int Tolerance = 2;

    /// <summary>
    /// Share of pixels that may differ by more than <see cref="Tolerance"/>: font updates re-rasterize a
    /// few glyph edges, while a real visual change (moved element, other text or color) affects far more.
    /// </summary>
    const double AllowedShare = 0.0001;

    static string References => Path.Combine(AppContext.BaseDirectory, "References");
    static string Diffs => Path.Combine(AppContext.BaseDirectory, "pixel-diffs");
    static bool Update => Environment.GetEnvironmentVariable("MOSAIK_UPDATE_REFERENCES") == "1";

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
        if (expected.Width != current.Width || expected.Height != current.Height)
        {
            Keep(name, actual, null);
            failures.Add(name + ": size " + current.Width + " x " + current.Height + " instead of " + expected.Width + " x " + expected.Height);
            return;
        }
        int different = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        var diff = new byte[expected.Data.Length];
        for (int i = 0; i < expected.Data.Length; i += 4)
        {
            bool same = true;
            for (int c = 0; c < 4; c++) same &= Math.Abs(expected.Data[i + c] - current.Data[i + c]) <= Tolerance;
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
        var stream = new MemoryStream(Shot.Encode(bitmap, false));
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    static void Save(BitmapSource bitmap, string file) => File.WriteAllBytes(file, Shot.Encode(bitmap, false));
}
