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

// Texterkennung von Windows (laeuft ohne Internet, in den Sprachen des Benutzerkontos)
static class ShotText
{
    public static async Task<string> Read(BitmapSource img)
    {
        try
        {
            var eng = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (eng == null) return null;
            // kleine Schrift wird vergroessert deutlich besser erkannt
            double f = Math.Min(2, global::Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)Math.Max(img.PixelWidth, img.PixelHeight));
            BitmapSource src = Math.Abs(f - 1) > 0.05 ? new TransformedBitmap(img, new ScaleTransform(f, f)) : img;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            var dec = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
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
            catch (COMException) { Thread.Sleep(40); }   // Zwischenablage kurz belegt
        }
    }

    public static string Summary(string text)
    {
        int lines = text.Split('\n').Length;
        return lines + (lines == 1 ? " Zeile · " : " Zeilen · ") + text.Replace("\r", "").Replace("\n", "").Length + " Zeichen";
    }
}
