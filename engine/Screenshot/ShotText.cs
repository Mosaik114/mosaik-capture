namespace MosaikCapture.Engine.Screenshot;

// Windows OCR (works offline, in the languages of the user profile).
static class ShotText
{
    public static async Task<string?> Read(BitmapSource img)
    {
        try
        {
            var eng = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (eng == null) return null;
            // small text is recognized much better when upscaled
            double f = Math.Min(2, Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)Math.Max(img.PixelWidth, img.PixelHeight));
            BitmapSource src = Math.Abs(f - 1) > 0.05 ? new TransformedBitmap(img, new ScaleTransform(f, f)) : img;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
            var bmp = await dec.GetSoftwareBitmapAsync();
            var res = await eng.RecognizeAsync(bmp);
            var sb = new StringBuilder();
            foreach (var line in res.Lines)
            {
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(line.Text);
            }
            return sb.ToString().Trim();
        }
        catch (Exception) { return null; }
    }

    public static void ToClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(40); }   // clipboard briefly busy
        }
    }

    public static string Summary(string text)
    {
        int lines = text.Split('\n').Length;
        return lines + (lines == 1 ? " Zeile · " : " Zeilen · ") + text.Replace("\r", "").Replace("\n", "").Length + " Zeichen";
    }
}
