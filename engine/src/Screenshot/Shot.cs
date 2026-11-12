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

// Mosaiks-Screenshot: Bildschirm einfrieren, Bereich aufziehen (oder Fenster anklicken,
// Leertaste = ganzer Bildschirm), beim Loslassen sofort in die Zwischenablage und als PNG
// nach Bilder\Mosaiks-Screenshots. Danach erscheint unten rechts eine Karte.
// Ausloeser: das Feld "Screenshot" im Aktionsring und Win+Shift+S (ueber Tastenbelegung.ahk).
static class Shot
{
    static ShotOverlay overlay;
    static ShotCard card;
    static ShotEditor editor;
    static ShotData last;   // letzte Aufnahme, solange Karte oder Editor offen sind

    public static string Folder
    {
        get { return CaptureSettings.ScreenshotFolder; }
    }

    public static void Start()
    {
        if (editor != null && editor.IsVisible) return;
        if (overlay == null) overlay = new ShotOverlay(Done);
        overlay.Open();
    }

    // Aus dem Ring: kurz warten, bis der ausgeblendete Ring auch wirklich vom Bildschirm ist
    public static void StartSoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += delegate { t.Stop(); Start(); };
        t.Start();
    }

    // Aus dem Ring: ganzer Monitor ohne Auswahl, ebenfalls erst nach dem Ausblenden des Rings
    public static void MonitorSoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += delegate
        {
            t.Stop();
            if (editor != null && editor.IsVisible) return;
            if (overlay == null) overlay = new ShotOverlay(Done);
            overlay.TakeMonitor();
        };
        t.Start();
    }

    // Aufnahme fertig: speichern, in die Zwischenablage legen, Karte zeigen
    static void Done(ShotData d)
    {
        if (editor != null && editor.IsVisible) return;
        d.Path = NewPath();
        last = d;
        Deliver(d, "In Zwischenablage kopiert");
    }

    static void Deliver(ShotData d, string heading)
    {
        if (card == null) card = new ShotCard(Edit, Pin, Gone);
        BitmapSource img = d.Image;
        card.Present(d, heading);
        Task.Factory.StartNew(delegate
        {
            byte[] png = Encode(img, false);
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(d.Path, png);
            return png;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { Program.Log("Screenshot speichern", t.Exception); card.Failed(); return; }
            ToClipboard(img, t.Result);
            card.Saved();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // "Bearbeiten" auf der Karte
    static void Edit()
    {
        if (last == null) return;
        if (editor == null) editor = new ShotEditor(Edited, EditCancelled, EditedPin);
        editor.Open(last);
        card.HideNow();
    }

    // "Anpinnen" auf der Karte: das Bild schwebt dort, wo es aufgenommen wurde
    static void Pin()
    {
        if (last != null) PinAt(last);
    }

    static void PinAt(ShotData d)
    {
        ShotPin.Open(d.Image, d.Vx + d.Cut.X, d.Vy + d.Cut.Y, d.Path);
    }

    static void EditedPin(ShotData d, BitmapSource img)
    {
        Edited(d, img);
        PinAt(d);
    }

    // Fertig im Editor: die Datei wird mit dem bearbeiteten Bild ueberschrieben
    static void Edited(ShotData d, BitmapSource img)
    {
        d.Image = img;
        Deliver(d, "Bearbeitet und kopiert");
    }

    static void EditCancelled(ShotData d)
    {
        card.Present(d, "In Zwischenablage kopiert");
        card.Saved();
    }

    // Karte ist weg: das grosse eingefrorene Bild freigeben
    static void Gone()
    {
        if (editor == null || !editor.IsVisible) last = null;
    }

    public static byte[] Encode(BitmapSource img, bool jpeg)
    {
        BitmapEncoder enc = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // "Speichern unter" fuer Editor und Pins: null = abgebrochen, "" = Fehler, sonst der Dateiname
    public static string SaveAs(Window owner, BitmapSource img, string suggested)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Mosaiks-Screenshot speichern unter", Filter = "PNG-Bild (*.png)|*.png|JPEG-Bild (*.jpg)|*.jpg",
            FileName = System.IO.Path.GetFileNameWithoutExtension(suggested ?? ""), InitialDirectory = Folder, DefaultExt = ".png", AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return null;
        string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        bool jpg = ext == ".jpg" || ext == ".jpeg";
        if (jpg) img = new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);   // JPEG kennt keine Transparenz
        try
        {
            File.WriteAllBytes(dlg.FileName, Encode(img, jpg));
            return System.IO.Path.GetFileName(dlg.FileName);
        }
        catch (Exception) { return ""; }
    }

    static string NewPath()
    {
        string stamp = "Mosaiks-Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string p = System.IO.Path.Combine(Folder, stamp + ".png");
        for (int i = 2; File.Exists(p); i++) p = System.IO.Path.Combine(Folder, stamp + " (" + i + ").png");
        return p;
    }

    // Als Bild und zusaetzlich als PNG, damit auch Browser und Chat-Apps es sauber einfuegen
    public static void ToClipboard(BitmapSource img, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(img);
        data.SetData("PNG", new MemoryStream(png));
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) { Thread.Sleep(40); }   // Zwischenablage kurz von anderer App belegt
        }
    }
}

// Eine Aufnahme: das ganze eingefrorene Bild (fuer den Editor, der den Ausschnitt noch
// aendern kann), der gewaehlte Ausschnitt in Geraetepixeln und der Monitor dazu
public class ShotData
{
    public BitmapSource Full, Image;
    public Int32Rect Cut;                  // relativ zur linken oberen Ecke des virtuellen Bildschirms
    public ShotOverlay.RECT Monitor, Work;
    public int Vx, Vy, Vw, Vh;
    public double Scale;                   // Geraetepixel pro WPF-Einheit
    public string Path;
}
