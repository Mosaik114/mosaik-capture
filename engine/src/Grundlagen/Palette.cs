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

// Farbpalette: warmes Creme bzw. warmes Dunkelgrau, Terrakotta als Akzent
static class Palette
{
    internal static bool? Force = null;   // nur fuer Pruefbilder: hell/dunkel unabhaengig von der Windows-Einstellung

    public static void Apply(ResourceDictionary r)
    {
        if (Force.HasValue) { Apply(r, Force.Value); return; }
        bool light = false;
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
            light = v is int && (int)v != 0;
        }
        catch (Exception) { }   // Einstellung nicht lesbar: dunkel bleiben
        Apply(r, light);
    }

    public static void Apply(ResourceDictionary r, bool light)
    {
        string[] keys = { "Bg", "Sel", "Hover", "Line", "Text", "Muted", "Accent", "Chip" };
        string[] colors = light
            ? new[] { "#FAF9F5", "#EEECE3", "#F4F2EA", "#E3E1D7", "#141413", "#7A786F", "#D97757", "#E6E3D8" }
            : new[] { "#262624", "#35342F", "#2E2D2A", "#3A3935", "#FAF9F5", "#A3A197", "#D97757", "#42413C" };
        for (int i = 0; i < keys.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            b.Freeze();
            r[keys[i]] = b;
        }
        // Akzent als zarte Flaeche (Hintergrund des Mikro-Symbols im Stumm-Zustand)
        var tint = new SolidColorBrush(Color.FromArgb(light ? (byte)0x26 : (byte)0x2E, 0xD9, 0x77, 0x57));
        tint.Freeze();
        r["AccentSoft"] = tint;
    }
}
