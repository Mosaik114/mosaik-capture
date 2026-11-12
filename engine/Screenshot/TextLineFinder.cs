namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Finds the words of a text line under a horizontal highlighter stroke, in device pixels of a screenshot.
/// Background = the most frequent color of the surroundings, ink = everything clearly different.
/// Pure image analysis, thread-safe.
/// </summary>
internal static class TextLineFinder
{
    /// <summary>How far around the stroke the image is examined (multiples of its half width, at least pixels).</summary>
    const double ReachFactor = 3, MinReach = 30, SideReachFactor = 3;
    /// <summary>Share of pixels the background color must cover, otherwise it is a photo.</summary>
    const double MinBackgroundShare = 0.3;
    /// <summary>Largest channel difference that still counts as background.</summary>
    const int InkThreshold = 60;
    /// <summary>The line core may lie this many pixels beyond the stroke's half width.</summary>
    const double LineSlack = 2;
    const int MinCoreHeight = 4;
    /// <summary>Completing a line: bridged blank rows (share of core height) and the largest total height.</summary>
    const double RowGapShare = 0.25, MaxLineHeightFactor = 2.2;
    /// <summary>Letters are up to about 0.15 font size apart, words from about 0.3: gaps below this share join.</summary>
    const double LetterJoinShare = 0.27;
    /// <summary>A word is hit if the stroke covers this share of it (or most of a short stroke lies on it).</summary>
    const double WordHitShare = 0.25, ShortStrokeShare = 0.6;

    /// <summary>The image section around the stroke as an ink mask.</summary>
    sealed class Ink
    {
        public Ink(int left, int top, int width, int height, bool[] mask)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Mask = mask;
        }

        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }
        public bool[] Mask { get; }

        public bool At(int x, int y) => Mask[y * Width + x];

        public bool AnyInRow(int y, int x0, int x1)
        {
            for (int x = x0; x < x1; x++)
                if (At(x, y)) return true;
            return false;
        }
    }

    /// <summary>The hit words of the line nearest to the stroke from x0 to x1 at height yc; empty if there is no text.</summary>
    /// <param name="image">The full screenshot.</param>
    /// <param name="x0">Stroke start (device pixels).</param>
    /// <param name="x1">Stroke end.</param>
    /// <param name="yc">Stroke height.</param>
    /// <param name="half">Half the stroke width.</param>
    public static Rect Find(BitmapSource image, double x0, double x1, double yc, double half)
    {
        var ink = InkAround(image, x0, x1, yc, half);
        if (ink == null) return Rect.Empty;
        int strokeLeft = Math.Max(0, (int)x0 - ink.Left), strokeRight = Math.Min(ink.Width, (int)x1 - ink.Left);
        if (!NearestLine(ink, strokeLeft, strokeRight, (int)yc - ink.Top, half, out int lineTop, out int lineBottom)) return Rect.Empty;
        if (!CompleteLine(ink, strokeLeft, strokeRight, ref lineTop, ref lineBottom)) return Rect.Empty;
        if (!HitWords(ink, strokeLeft, strokeRight, lineTop, lineBottom, out int wordsLeft, out int wordsRight)) return Rect.Empty;
        return new Rect(ink.Left + wordsLeft, ink.Top + lineTop, wordsRight - wordsLeft + 1, lineBottom - lineTop + 1);
    }

    // The section around the stroke; null if it is too small or has no calm background (a photo).
    static Ink? InkAround(BitmapSource image, double x0, double x1, double yc, double half)
    {
        double reach = Math.Max(half * ReachFactor, MinReach);
        int top = Math.Max(0, (int)(yc - reach)), bottom = Math.Min(image.PixelHeight, (int)(yc + reach));
        int left = Math.Max(0, (int)(x0 - reach * SideReachFactor)), right = Math.Min(image.PixelWidth, (int)(x1 + reach * SideReachFactor));
        int width = right - left, height = bottom - top;
        if (width < 4 || height < 8) return null;
        var section = new FormatConvertedBitmap(new CroppedBitmap(image, new Int32Rect(left, top, width, height)), PixelFormats.Bgr32, null, 0);
        var pixels = new byte[width * height * 4];
        section.CopyPixels(pixels, width * 4, 0);
        if (!TryBackground(pixels, out int blue, out int green, out int red)) return null;
        var mask = new bool[width * height];
        for (int i = 0; i < mask.Length; i++)
        {
            int p = i * 4;
            mask[i] = Math.Max(Math.Abs(pixels[p] - blue), Math.Max(Math.Abs(pixels[p + 1] - green), Math.Abs(pixels[p + 2] - red))) > InkThreshold;
        }
        return new Ink(left, top, width, height, mask);
    }

    // The most frequent color at 5 bits per channel, if it covers enough of the section.
    static bool TryBackground(byte[] pixels, out int blue, out int green, out int red)
    {
        var histogram = new int[32768];
        for (int i = 0; i < pixels.Length; i += 4) histogram[(pixels[i] >> 3) | (pixels[i + 1] >> 3) << 5 | (pixels[i + 2] >> 3) << 10]++;
        int mode = 0;
        for (int i = 1; i < histogram.Length; i++) if (histogram[i] > histogram[mode]) mode = i;
        blue = (mode & 31) * 8 + 4;
        green = (mode >> 5 & 31) * 8 + 4;
        red = (mode >> 10) * 8 + 4;
        return histogram[mode] >= pixels.Length / 4 * MinBackgroundShare;
    }

    // Rows with ink below the stroke form lines (a single blank row inside a line is bridged); the nearest counts.
    static bool NearestLine(Ink ink, int strokeLeft, int strokeRight, int strokeRow, double half, out int lineTop, out int lineBottom)
    {
        var rowInk = new bool[ink.Height];
        for (int y = 0; y < ink.Height; y++) rowInk[y] = ink.AnyInRow(y, strokeLeft, strokeRight);
        lineTop = lineBottom = -1;
        double bestDistance = double.MaxValue;
        for (int y = 0; y < ink.Height;)
        {
            if (!rowInk[y]) { y++; continue; }
            int start = y;
            while (y < ink.Height && (rowInk[y] || (y + 1 < ink.Height && rowInk[y + 1]))) y++;
            int end = y - 1;
            double distance = strokeRow < start ? start - strokeRow : strokeRow > end ? strokeRow - end : 0;
            if (distance < bestDistance) { bestDistance = distance; lineTop = start; lineBottom = end; }
        }
        return lineTop >= 0 && bestDistance <= half + LineSlack && lineBottom - lineTop + 1 >= MinCoreHeight;
    }

    // If the stroke only hits small letters (w, i, c), ascenders, i-dots and descenders are missing: search a wider
    // band, bridge small gaps (i-dot) and stay at most about twice the core height so no neighbouring line is included.
    // False if the line reaches the section's border (then it is no text line).
    static bool CompleteLine(Ink ink, int strokeLeft, int strokeRight, ref int lineTop, ref int lineBottom)
    {
        int core = lineBottom - lineTop + 1;
        int bandLeft = Math.Max(0, strokeLeft - 2 * core), bandRight = Math.Min(ink.Width, strokeRight + 2 * core);
        int gap = Math.Max(2, (int)Math.Round(core * RowGapShare)), maxHeight = (int)(core * MaxLineHeightFactor);
        for (int y = lineTop - 1, blank = 0; y >= 0 && blank <= gap && lineBottom - y + 1 <= maxHeight; y--)
            if (ink.AnyInRow(y, bandLeft, bandRight)) { lineTop = y; blank = 0; } else blank++;
        for (int y = lineBottom + 1, blank = 0; y < ink.Height && blank <= gap && y - lineTop + 1 <= maxHeight; y++)
            if (ink.AnyInRow(y, bandLeft, bandRight)) { lineBottom = y; blank = 0; } else blank++;
        return lineTop != 0 && lineBottom != ink.Height - 1;
    }

    // Words are runs of ink columns in the line, small gaps (letter spacing) belong to them; keeps the words the stroke hits.
    static bool HitWords(Ink ink, int strokeLeft, int strokeRight, int lineTop, int lineBottom, out int wordsLeft, out int wordsRight)
    {
        var columnInk = new bool[ink.Width];
        for (int x = 0; x < ink.Width; x++)
            for (int y = lineTop; y <= lineBottom; y++)
                if (ink.At(x, y)) { columnInk[x] = true; break; }
        int join = Math.Max(2, (int)Math.Round((lineBottom - lineTop + 1) * LetterJoinShare));
        wordsLeft = wordsRight = -1;
        for (int x = 0; x < ink.Width;)
        {
            if (!columnInk[x]) { x++; continue; }
            int start = x, lastInk = x;
            while (x < ink.Width && x - lastInk <= join) { if (columnInk[x]) lastInk = x; x++; }
            int end = lastInk;
            if (IsHit(start, end, strokeLeft, strokeRight))
            {
                if (wordsLeft < 0) wordsLeft = start;
                wordsRight = end;
            }
        }
        return wordsLeft > 0 && wordsRight != ink.Width - 1;   // a word at the border may be cut off
    }

    static bool IsHit(int start, int end, int strokeLeft, int strokeRight)
    {
        int wordWidth = end - start + 1, strokeWidth = strokeRight - strokeLeft;
        double overlap = Math.Min(end + 1, strokeRight) - Math.Max(start, strokeLeft);
        return overlap > WordHitShare * wordWidth || (overlap > 0 && strokeWidth < wordWidth && overlap >= ShortStrokeShare * strokeWidth);
    }
}
