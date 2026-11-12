using WinOcr = Windows.Media.Ocr;

namespace MosaikCapture.Engine.Screenshot;

internal enum TextRecognitionStatus
{
    /// <summary>Text was found.</summary>
    Recognized,
    /// <summary>OCR worked but the image contains no text.</summary>
    NoText,
    /// <summary>No OCR language is installed for the user's languages.</summary>
    Unavailable,
    /// <summary>OCR failed (see the error log).</summary>
    Failed,
}

internal readonly struct TextRecognitionResult
{
    public TextRecognitionResult(TextRecognitionStatus status, string text = "")
    {
        Status = status;
        Text = text;
    }

    public TextRecognitionStatus Status { get; }
    public string Text { get; }

    /// <summary>What to tell the user when no text can be copied.</summary>
    public string FailureMessage => Status == TextRecognitionStatus.NoText ? UiText.Common.NoTextFound : UiText.Common.TextRecognitionUnavailable;
}

/// <summary>Windows OCR (works offline, in the languages of the user profile).</summary>
internal static class TextRecognition
{
    public static async Task<TextRecognitionResult> ReadAsync(BitmapSource image)
    {
        try
        {
            var engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return new TextRecognitionResult(TextRecognitionStatus.Unavailable);
            // small text is recognized much better when upscaled
            double factor = Math.Min(2, WinOcr.OcrEngine.MaxImageDimension / (double)Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource scaled = Math.Abs(factor - 1) > 0.05 ? new TransformedBitmap(image, new ScaleTransform(factor, factor)) : image;
            var png = new MemoryStream(Screenshots.Encode(scaled, false));
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(WindowsRuntimeStreamExtensions.AsRandomAccessStream(png));
            var bitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await engine.RecognizeAsync(bitmap);
            string text = string.Join("\r\n", result.Lines.Select(line => line.Text)).Trim();
            return text.Length > 0 ? new TextRecognitionResult(TextRecognitionStatus.Recognized, text) : new TextRecognitionResult(TextRecognitionStatus.NoText);
        }
        catch (Exception ex)   // WinRT reports failures with many exception types
        {
            ErrorLog.Write("Recognizing text", ex);
            return new TextRecognitionResult(TextRecognitionStatus.Failed);
        }
    }

    public static void ToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(40); }   // clipboard briefly held by another app
        }
    }

    /// <summary>"3 Zeilen · 42 Zeichen"</summary>
    public static string Summary(string text) => UiText.Common.TextSummary(text.Split('\n').Length, text.Replace("\r", "").Replace("\n", "").Length);
}
