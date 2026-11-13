using System.Windows.Controls.Primitives;

namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// A pinned screenshot: floats above everything, exactly where it was captured.
/// Drag = move, mouse wheel = zoom, Ctrl+wheel = opacity, double click / Esc = close,
/// right click = menu (copy, save as, original size, close).
/// </summary>
internal sealed class PinnedScreenshot : Window
{
    const double ShadowRoom = 14;
    const double MinZoom = 0.1, MaxZoom = 5, ZoomStep = 1.1;
    const uint MoveFlags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
    static readonly List<PinnedScreenshot> open = new();
    static readonly SolidColorBrush CloseButtonBackground = Palette.Frozen(0xCC, 0x26, 0x26, 0x24);

    readonly BitmapSource image;
    readonly string fileName;
    readonly IntPtr hwnd;
    readonly Image view;
    readonly Border frame, closeButton, chip;
    readonly TextBlock chipText;
    readonly Popup menu;
    readonly DispatcherTimer chipTimer;
    double scale = 1, zoom = 1;

    public static void Open(BitmapSource image, int x, int y, string fileName)
    {
        var pin = new PinnedScreenshot(image, fileName);
        open.Add(pin);
        pin.PlaceAt(x, y);
    }

    public static void CloseAll()
    {
        foreach (var pin in open.ToArray()) pin.Close();
    }

    PinnedScreenshot(BitmapSource pinnedImage, string file)
    {
        image = pinnedImage;
        fileName = file;
        ToolWindows.Setup(this, UiText.Titles.Pin);
        SizeToContent = SizeToContent.WidthAndHeight;

        view = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
        closeButton = BuildCloseButton();
        chipText = new TextBlock { FontSize = 11.5, FontFamily = Palette.UiFont, FontWeight = FontWeights.SemiBold };
        chip = BuildChip();
        chipTimer = new DispatcherTimer { Interval = Motion.FeedbackChip };
        chipTimer.Tick += delegate { chipTimer.Stop(); chip.Visibility = Visibility.Collapsed; };

        var layers = new Grid();
        layers.Children.Add(view);
        // the border lies on top of the image instead of around it so the image sits pixel-exactly where it was
        var outline = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        outline.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        layers.Children.Add(outline);
        layers.Children.Add(closeButton);
        layers.Children.Add(chip);
        frame = new Border
        {
            Margin = new Thickness(ShadowRoom),
            Child = layers,
            Cursor = Cursors.SizeAll,
            Effect = ToolWindows.Shadow(16, 3, 0.45)
        };
        Content = frame;
        menu = BuildMenu();

        HookUpInput();
        Closed += delegate { open.Remove(this); view.Source = null; };
        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_TOOLWINDOW);   // not in Alt+Tab
    }

    // ---------- Building ----------

    Border BuildCloseButton()
    {
        var cross = new TextBlock { Text = IconGlyphs.Cancel, FontSize = 10, FontFamily = Palette.IconFont, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(6),
            Child = cross,
            Cursor = Cursors.Hand,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(CloseButtonBackground.Color),
            ToolTip = UiText.Pin.CloseTip
        };
        button.MouseEnter += delegate { button.SetResourceReference(Border.BackgroundProperty, Palette.AccentFill); };
        button.MouseLeave += delegate { button.Background = new SolidColorBrush(CloseButtonBackground.Color); };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        return button;
    }

    // Short feedback in the bottom left: zoom, opacity, "copied".
    Border BuildChip()
    {
        chipText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var feedback = ToolWindows.Chip(chipText);
        feedback.Margin = new Thickness(8);
        feedback.HorizontalAlignment = HorizontalAlignment.Left;
        feedback.VerticalAlignment = VerticalAlignment.Bottom;
        feedback.IsHitTestVisible = false;
        feedback.Visibility = Visibility.Collapsed;
        return feedback;
    }

    Popup BuildMenu()
    {
        var items = new StackPanel();
        items.Children.Add(MenuItem(IconGlyphs.Copy, UiText.Pin.Copy, UiText.Pin.CopyShortcut, Copy));
        items.Children.Add(MenuItem(IconGlyphs.Save, UiText.Pin.SaveAs, UiText.Pin.SaveAsShortcut, SaveAs));
        items.Children.Add(MenuItem(IconGlyphs.ZoomOriginal, UiText.Pin.OriginalSize, "0", delegate { ZoomTo(1, atPointer: false); }));
        items.Children.Add(MenuSeparator());
        items.Children.Add(MenuItem(IconGlyphs.Cancel, UiText.Pin.Close, "Esc", Close));
        items.Children.Add(MenuItem(IconGlyphs.ChromeClose, UiText.Pin.CloseAll, "", CloseAll));
        var box = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            Child = items,
            Margin = new Thickness(0, 0, 12, 12),
            Effect = Metrics.MenuShadow()
        };
        box.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        box.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        return new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.MousePoint,
            PopupAnimation = PopupAnimation.Fade,
            Child = box
        };
    }

    Border MenuItem(string glyph, string label, string shortcut, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13, FontFamily = Palette.IconFont, Width = 26, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var key = new TextBlock { Text = shortcut, FontSize = 11.5, FontFamily = Palette.UiFont, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        key.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(key, Dock.Right);
        var text = new TextBlock { Text = label, FontSize = 12.5, FontFamily = Palette.UiFont, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(icon);
        row.Children.Add(key);
        row.Children.Add(text);
        var item = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 12, 7), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        item.MouseEnter += delegate { item.SetResourceReference(Border.BackgroundProperty, Palette.Hover); };
        item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += delegate { menu.IsOpen = false; click(); };
        return item;
    }

    static Shapes.Rectangle MenuSeparator()
    {
        var line = new Shapes.Rectangle { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        line.SetResourceReference(Shapes.Shape.FillProperty, Palette.Line);
        return line;
    }

    void HookUpInput()
    {
        frame.MouseEnter += delegate { FadeCloseButton(1); };
        frame.MouseLeave += delegate { if (!menu.IsOpen) FadeCloseButton(0); };
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            Activate();
            try { DragMove(); }
            catch (InvalidOperationException) { }   // mouse button already released
        };
        frame.MouseRightButtonUp += (_, e) => { e.Handled = true; Activate(); menu.IsOpen = true; };
        menu.Closed += delegate { if (!frame.IsMouseOver) FadeCloseButton(0); };
        MouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ChangeOpacity(e.Delta > 0 ? 0.1 : -0.1);
            else ZoomTo(zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), atPointer: true);
        };
        KeyDown += (_, e) => { if (OnKey(e.Key, (Keyboard.Modifiers & ModifierKeys.Control) != 0)) e.Handled = true; };
    }

    bool OnKey(Key key, bool control)
    {
        if (key == Key.Escape) Close();
        else if (control && key == Key.C) Copy();
        else if (control && key == Key.S) SaveAs();
        else if (key is Key.D0 or Key.NumPad0) ZoomTo(1, atPointer: false);
        else if (key is Key.OemPlus or Key.Add) ZoomTo(zoom * ZoomStep, atPointer: false);
        else if (key is Key.OemMinus or Key.Subtract) ZoomTo(zoom / ZoomStep, atPointer: false);
        else return false;
        return true;
    }

    void FadeCloseButton(double opacity) =>
        closeButton.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, Motion.HoverFade));

    // ---------- Placement and zoom ----------

    // (x, y) = top-left corner of the image in device pixels.
    void PlaceAt(int x, int y)
    {
        Left = -20000;
        Top = -20000;
        view.Width = image.PixelWidth;
        view.Height = image.PixelHeight;
        Show();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, x, y, 0, 0, MoveFlags);   // move to the target monitor first so the scale is right
        scale = ToolWindows.Scale(hwnd);
        ApplyZoom();
        UpdateLayout();
        int shadow = (int)Math.Round(ShadowRoom * scale);
        NativeMethods.MoveTopmost(hwnd, x - shadow, y - shadow);
        frame.Opacity = 0;
        frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.FadeIn));
    }

    // 100 % = one image pixel per screen pixel, as captured.
    void ApplyZoom()
    {
        view.Width = image.PixelWidth * zoom / scale;
        view.Height = image.PixelHeight * zoom / scale;
    }

    // With the pointer, the point under it stays put; otherwise the top-left corner does.
    void ZoomTo(double target, bool atPointer)
    {
        target = ClampZoom(target);
        NativeMethods.GetWindowRect(hwnd, out RECT window);
        int shadow = (int)Math.Round(ShadowRoom * scale);
        double imageLeft = window.left + shadow, imageTop = window.top + shadow, anchorX = imageLeft, anchorY = imageTop;
        if (atPointer)
        {
            NativeMethods.GetCursorPos(out var pointer);
            anchorX = pointer.x;
            anchorY = pointer.y;
        }
        double factor = target / zoom;
        zoom = target;
        ApplyZoom();
        UpdateLayout();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, (int)Math.Round(anchorX - (anchorX - imageLeft) * factor) - shadow,
            (int)Math.Round(anchorY - (anchorY - imageTop) * factor) - shadow, 0, 0, MoveFlags);
        ShowFeedback(UiText.Pin.Zoom(Math.Round(zoom * 100)));
    }

    /// <summary>Limits the zoom to 10 %..500 % and snaps to 100 % when close.</summary>
    internal static double ClampZoom(double zoom)
    {
        zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
        return Math.Abs(zoom - 1) < 0.05 ? 1 : zoom;
    }

    /// <summary>Opacity in steps of 10 %, at least 20 %.</summary>
    internal static double StepOpacity(double opacity, double step) => Math.Max(0.2, Math.Min(1, Math.Round((opacity + step) * 10) / 10));

    void ChangeOpacity(double step)
    {
        frame.Opacity = StepOpacity(frame.Opacity, step);
        ShowFeedback(UiText.Pin.Opacity(Math.Round(frame.Opacity * 100)));
    }

    void ShowFeedback(string text)
    {
        chipText.Text = text;
        chip.Visibility = Visibility.Visible;
        chipTimer.Stop();
        chipTimer.Start();
    }

    void Copy() => CopyAsync().OnFailure("Copying a pinned screenshot", () => ShowFeedback(UiText.Common.CopyFailed));

    async Task CopyAsync()
    {
        bool copied = await ClipboardWriter.SetImageAsync(image, Screenshots.Encode(image, false));
        ShowFeedback(copied ? UiText.Pin.Copied : UiText.Common.CopyFailed);
    }

    void SaveAs()
    {
        var result = Screenshots.SaveAs(this, image, fileName, out _);
        if (result == SaveAsResult.Saved) ShowFeedback(UiText.Pin.Saved);
        else if (result == SaveAsResult.Failed) ShowFeedback(UiText.Common.CouldNotSave);
    }
}
