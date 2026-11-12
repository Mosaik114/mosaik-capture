namespace MosaikCapture.Engine.Screenshot;

/// <summary>
/// Screenshot editor: the frozen screen stays dimmed, the crop is bright, has handles for
/// re-cropping and the toolbar below it. Related tools share a button; a second click on the
/// active button shows the choices.
/// </summary>
/// <remarks>
/// Keys: V move, A arrow, R rectangle, E ellipse, L line, P pen, M highlighter, S spotlight,
/// T text, B text with background, N number, X pixelate, W blur, 1-6 color, +/- or mouse wheel size,
/// Ctrl+Z / Ctrl+Y, Del, Ctrl+C copy, Ctrl+Shift+C copy text, Ctrl+S save as, Enter done, F3 pin, Esc cancel.
/// Pen: hold still at the end = clean shape; highlighter: snaps onto the text line it crosses.
/// This file holds the fields and the construction of the window and its toolbar; the other parts are
/// ScreenshotEditor.Mouse (open, layout, pointer), .Markup (placing markup), .Shapes (shape recognition)
/// and .Result (undo, keyboard, result).
/// The state lives in plain models: <see cref="EditorDocument"/> (markup items, crop, selection, text draft,
/// undo/redo), <see cref="EditorInteraction"/> (tool, gesture, discard question), <see cref="PenSnap"/> and
/// <see cref="InkStyle"/>. The window draws the document (one element per item, following its events) and
/// routes input to the models.
/// </remarks>
internal sealed partial class ScreenshotEditor : Window
{
    internal enum Tool { Move, Arrow, Box, Ellipse, Line, Pen, Marker, Spot, Text, Label, Number, Pixel, Blur }
    static readonly Key[] ToolKeys = { Key.V, Key.A, Key.R, Key.E, Key.L, Key.P, Key.M, Key.S, Key.T, Key.B, Key.N, Key.X, Key.W };

    // Button tags that drive Paint()
    const string PrimaryTag = "primary", ActiveTag = "on";

    sealed class ToolGroup
    {
        public ToolGroup(Tool[] tools)
        {
            Tools = tools;
            Current = tools[0];
        }

        public Tool[] Tools { get; }
        /// <summary>The tool picked last; its icon is shown on the button.</summary>
        public Tool Current { get; set; }
        public Border Button { get; set; } = null!;   // created right after the group
        public Grid IconHost { get; } = new();
        public SolidColorBrush IconBrush { get; } = new(Colors.White);
    }

    static readonly Cursor[] HandleCursors =
    {
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE
    };
    static readonly Point Offscreen = new(-1e6, -1e6);

    readonly Action<ScreenCapture, BitmapSource> onDone, onPinned;
    readonly Action<ScreenCapture> onCancelled;
    readonly IntPtr hwnd;
    readonly Canvas root, markup;
    readonly Image frozenImage;
    readonly Shapes.Path dimming;
    readonly Shapes.Rectangle frame;
    readonly Border[] resizeHandles = new Border[CropGeometry.HandleCount];
    readonly Border sizeChip, toolbar, tooltip, notice, toolMenu;
    readonly TextBlock sizeText, tooltipText, noticeText;
    readonly StackPanel toolbarRow, menuItems;
    readonly List<ToolGroup> groups = new();
    readonly List<Shapes.Ellipse> colorRings = new();
    readonly List<Border> widthButtons = new();
    readonly InkStyle style = new();

    // The model: items, crop, selection, text draft and undo live in the document; the pen-hold snap in
    // its own model. The window keeps only what it needs to draw and to route input.
    readonly EditorDocument document = new();
    readonly PenSnap snap = new();
    readonly Dictionary<MarkupItem, FrameworkElement> elements = new();
    readonly Dictionary<FrameworkElement, MarkupItem> itemsByElement = new();
    /// <summary>Pen or highlighter held still this long: the stroke snaps to a shape.</summary>
    static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(450);
    /// <summary>How long a notice (copied, saved, ...) stays.</summary>
    static readonly TimeSpan NoticeDuration = TimeSpan.FromMilliseconds(1800);

    readonly DispatcherTimer holdTimer = new() { Interval = HoldDelay };           // pen/highlighter held still
    readonly DispatcherTimer noticeTimer = new() { Interval = NoticeDuration };
    ToolGroup? openMenuGroup;
    Point holdPoint;
    Shapes.Path? spotlightShade;                  // dimming for the spotlights
    FrameworkElement? textHost;                   // the text box itself or the plate around it

    ScreenCapture capture = null!;                // set by Load(), cleared by HideAndReset()
    double scale = 1;                             // device pixels per WPF unit
    readonly EditorInteraction interaction = new();
    FrameworkElement? numberPreview;              // semi-transparent preview of the next number at the pointer
    string? numberPreviewKey;
    Point pointer;
    Shapes.Path? stroke;
    PolyLineSegment strokeLine = null!;           // points of the current pen or highlighter stroke
    TextBox? textBox;

    public ScreenshotEditor(Action<ScreenCapture, BitmapSource> onDone, Action<ScreenCapture> onCancelled, Action<ScreenCapture, BitmapSource> onPinned)
    {
        this.onDone = onDone;
        this.onCancelled = onCancelled;
        this.onPinned = onPinned;
        ConfigureWindow();

        root = new Canvas { ClipToBounds = true };
        frozenImage = new Image { Stretch = Stretch.Fill };
        dimming = new Shapes.Path { Fill = Palette.Dimming, IsHitTestVisible = false };
        markup = new Canvas();
        frame = new Shapes.Rectangle { StrokeThickness = 2, IsHitTestVisible = false };
        frame.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
        sizeText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Palette.UiFont };
        sizeChip = BuildSizeChip();
        toolbarRow = new StackPanel { Orientation = Orientation.Horizontal };
        tooltipText = new TextBlock { FontSize = 12, FontFamily = Palette.UiFont };
        tooltip = BuildTooltip();
        toolbar = BuildToolbar();
        menuItems = new StackPanel();
        toolMenu = BuildToolMenu();
        noticeText = new TextBlock { FontSize = 12.5, FontFamily = Palette.UiFont, FontWeight = FontWeights.SemiBold };
        notice = BuildNotice();
        ComposeSurface();

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.AddExtendedStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW);
        HookInput();
        FollowDocument();
    }

    // The markup layer mirrors the document: its children are the items' elements in document order,
    // the spotlight dimming right above the covers, and while drawing or typing one element on top.
    void FollowDocument()
    {
        document.Inserted += (item, index) =>
        {
            if (spotlightShade != null) markup.Children.Remove(spotlightShade);
            markup.Children.Insert(index, elements[item]);
            RefreshSpotlightShade(null);
        };
        document.Removed += (item, _) =>
        {
            markup.Children.Remove(elements[item]);
            RefreshSpotlightShade(null);
        };
        document.Moved += item =>
        {
            var offset = (TranslateTransform)elements[item].RenderTransform;
            offset.X = item.Offset.X;
            offset.Y = item.Offset.Y;
            if (item.Kind == MarkupKind.Spotlight) RefreshSpotlightShade(null);
        };
        document.CropChanged += LayoutCrop;
    }

    // Draws the markup of a new item and hands the item to the document.
    void AddMarkup(FrameworkElement element, MarkupKind kind)
    {
        var item = new MarkupItem(kind, AreaOf(element));
        elements[item] = element;
        itemsByElement[element] = item;
        document.Add(item);
    }

    static Rect AreaOf(FrameworkElement element)
    {
        double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
        left = double.IsNaN(left) ? 0 : left;
        top = double.IsNaN(top) ? 0 : top;
        if (element is Shapes.Path path && path.Data != null)
        {
            Rect bounds = path.Data.Bounds;
            if (!bounds.IsEmpty) bounds.Offset(left, top);
            return bounds;
        }
        return new Rect(left, top, double.IsNaN(element.Width) ? 0 : element.Width, double.IsNaN(element.Height) ? 0 : element.Height);
    }

    // A new screenshot or a closed editor: no markup elements left.
    void ClearMarkup()
    {
        markup.Children.Clear();
        elements.Clear();
        itemsByElement.Clear();
    }

    void ConfigureWindow()
    {
        Title = UiText.Titles.Editor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Palette.Apply(Resources);
    }

    // Layers from bottom to top: screenshot, dimming, markup, crop frame and handles, then the controls.
    void ComposeSurface()
    {
        root.Children.Add(frozenImage);
        root.Children.Add(dimming);
        root.Children.Add(markup);
        root.Children.Add(frame);
        AddResizeHandles();
        root.Children.Add(sizeChip);
        root.Children.Add(toolbar);
        root.Children.Add(toolMenu);
        root.Children.Add(tooltip);
        root.Children.Add(notice);
        Content = root;
    }

    // Every input handler is guarded: an error in one command must not leave the editor half-changed.
    void HookInput()
    {
        holdTimer.Tick += delegate { holdTimer.Stop(); Guarded("Snapping a stroke", PenHeld); };
        noticeTimer.Tick += delegate { noticeTimer.Stop(); notice.Visibility = Visibility.Hidden; interaction.DisarmDiscard(); };
        MouseLeftButtonDown += (_, e) => Guarded("Pressing in the editor", () => PointerDown(e.GetPosition(root)));
        MouseMove += (_, e) => Guarded("Moving in the editor", () => PointerMoved(e.GetPosition(root)));
        MouseLeftButtonUp += (_, _) => Guarded("Releasing in the editor", PointerUp);
        MouseLeave += delegate { pointer = Offscreen; UpdateNumberPreview(); };
        MouseWheel += (_, e) => Guarded("Changing the width", () => { if (!document.Typing && interaction.Idle) StepWidth(e.Delta > 0 ? 1 : -1); });
        PreviewKeyDown += (_, e) => Guarded("Editor command " + e.Key, () => OnKey(e));
    }

    /// <summary>Runs an input handler; on an unexpected error the current gesture is dropped and the user told.</summary>
    void Guarded(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)   // last line of defence for one command: the editor stays usable
        {
            ErrorLog.Write(what, ex);
            if (IsMouseCaptured) ReleaseMouseCapture();
            holdTimer.Stop();
            interaction.Reset();
            ShowNotice(UiText.Editor.CommandFailed);
        }
    }

    /// <summary>After an error elsewhere in the screenshot flow: closes without reporting back.</summary>
    internal void Abandon()
    {
        if (IsVisible) HideAndReset();
    }

    // ---------- Building ----------

    // Eight round handles around the crop; dragging one resizes the crop.
    void AddResizeHandles()
    {
        for (int i = 0; i < resizeHandles.Length; i++)
        {
            var dot = new Shapes.Ellipse { Width = Metrics.HandleSize, Height = Metrics.HandleSize, StrokeThickness = Metrics.HandleStroke };
            dot.SetResourceReference(Shapes.Shape.FillProperty, Palette.Text);
            dot.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);
            var handle = new Border { Width = Metrics.HandleHitSize, Height = Metrics.HandleHitSize, Background = Brushes.Transparent, Child = dot, Cursor = HandleCursors[i] };   // larger hit area
            int index = i;
            handle.MouseLeftButtonDown += (_, e) =>
            {
                CommitText();
                interaction.Begin(PointerMode.ResizeCrop);
                interaction.Handle = index;
                interaction.CropAtPress = document.Crop;
                CaptureMouse();
                e.Handled = true;
            };
            resizeHandles[i] = handle;
            root.Children.Add(handle);
        }
    }

    Border BuildSizeChip()
    {
        sizeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var chip = ToolWindows.Chip(sizeText);
        chip.IsHitTestVisible = false;
        return chip;
    }

    Border BuildToolbar()
    {
        AddToolGroup(Tool.Move);
        AddSeparator();
        AddToolGroup(Tool.Arrow);
        AddToolGroup(Tool.Box, Tool.Ellipse, Tool.Line);
        AddToolGroup(Tool.Pen);
        AddToolGroup(Tool.Marker, Tool.Spot);
        AddToolGroup(Tool.Text, Tool.Label);
        AddToolGroup(Tool.Number);
        AddToolGroup(Tool.Pixel, Tool.Blur);
        AddSeparator();
        AddColorButtons();
        AddWidthButtons();
        AddSeparator();
        toolbarRow.Children.Add(IconButton(IconGlyphs.Undo, UiText.Editor.Undo, Undo, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.Redo, UiText.Editor.Redo, Redo, false));
        AddSeparator();
        toolbarRow.Children.Add(IconButton(IconGlyphs.Save, UiText.Editor.SaveAs, SaveAs, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.Copy, UiText.Editor.Copy, CopyNow, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.TextRecognition, UiText.Editor.CopyText, CopyText, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.Pin, UiText.Editor.Pin, delegate { Finish(pin: true); }, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.Cancel, UiText.Editor.Cancel, Cancel, false));
        toolbarRow.Children.Add(IconButton(IconGlyphs.Accept, UiText.Editor.Done, delegate { Finish(pin: false); }, true));
        var bar = new Border
        {
            CornerRadius = new CornerRadius(Metrics.ToolbarRadius),
            Padding = new Thickness(Metrics.ToolbarPadding),
            BorderThickness = new Thickness(1),
            Child = toolbarRow,
            Cursor = Cursors.Arrow,
            Effect = Metrics.ToolbarShadow()
        };
        bar.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        bar.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        bar.MouseLeftButtonDown += (_, e) => e.Handled = true;   // clicks on the toolbar do not draw
        return bar;
    }

    void AddColorButtons()
    {
        for (int i = 0; i < InkStyle.Colors.Length; i++)
        {
            var button = new Grid { Width = Metrics.ColorButtonWidth, Height = Metrics.ButtonSize, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var ring = new Shapes.Ellipse { Width = Metrics.ColorRingSize, Height = Metrics.ColorRingSize, StrokeThickness = Metrics.ColorRingThickness, Visibility = Visibility.Hidden };
            ring.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Text);
            button.Children.Add(ring);
            button.Children.Add(new Shapes.Ellipse { Width = Metrics.ColorDotSize, Height = Metrics.ColorDotSize, Fill = new SolidColorBrush(InkStyle.Colors[i]) });
            colorRings.Add(ring);
            int index = i;
            button.MouseLeftButtonUp += delegate { SetColor(index); };
            button.MouseEnter += delegate { ShowTooltip(button, UiText.Editor.ColorTip(index + 1)); };
            button.MouseLeave += delegate { tooltip.Visibility = Visibility.Hidden; };
            toolbarRow.Children.Add(button);
        }
    }

    void AddWidthButtons()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < Metrics.WidthDots.Length; i++)
        {
            var dot = new Shapes.Ellipse { Width = Metrics.WidthDots[i], Height = Metrics.WidthDots[i] };
            dot.SetResourceReference(Shapes.Shape.FillProperty, Palette.Text);
            var button = new Border { Width = Metrics.WidthButtonSize, Height = Metrics.WidthButtonSize, CornerRadius = new CornerRadius(Metrics.WidthButtonRadius), Child = dot, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            int index = i;
            button.MouseLeftButtonUp += delegate { SetWidth(index); };
            button.MouseEnter += delegate { Paint(button, true); ShowTooltip(button, UiText.Editor.WidthNames[index] + UiText.Editor.WidthHint); };
            button.MouseLeave += delegate { Paint(button, false); tooltip.Visibility = Visibility.Hidden; };
            widthButtons.Add(button);
            buttons.Children.Add(button);
        }
        toolbarRow.Children.Add(buttons);
    }

    Border BuildToolMenu()
    {
        var menu = new Border
        {
            CornerRadius = new CornerRadius(Metrics.MenuRadius),
            Padding = new Thickness(Metrics.MenuPadding),
            BorderThickness = new Thickness(1),
            Child = menuItems,
            Cursor = Cursors.Arrow,
            MinWidth = Metrics.MenuMinWidth,
            Visibility = Visibility.Hidden,
            Effect = Metrics.MenuShadow()
        };
        menu.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        menu.SetResourceReference(Border.BorderBrushProperty, Palette.Line);
        menu.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return menu;
    }

    Border BuildTooltip()
    {
        tooltipText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var chip = ToolWindows.Chip(tooltipText);
        chip.IsHitTestVisible = false;
        chip.Visibility = Visibility.Hidden;
        chip.Effect = Metrics.ChipShadow();
        return chip;
    }

    // Short messages above or below the toolbar (saved, the chosen width, ...).
    Border BuildNotice()
    {
        noticeText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var chip = ToolWindows.Chip(noticeText);
        chip.CornerRadius = new CornerRadius(Metrics.NoticeRadius);
        chip.Padding = Metrics.NoticePadding;
        chip.IsHitTestVisible = false;
        chip.Visibility = Visibility.Hidden;
        chip.Effect = Metrics.ChipShadow();
        return chip;
    }

    // ---------- Toolbar ----------

    static TextBlock Glyph(string glyph, Brush? foreground) => new()
    {
        Text = glyph,
        FontSize = Metrics.IconSize,
        FontFamily = Palette.IconFont,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    void AddSeparator()
    {
        var line = new Shapes.Rectangle { Width = 1, Height = Metrics.SeparatorHeight, Margin = new Thickness(Metrics.SeparatorGap, 0, Metrics.SeparatorGap, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Shapes.Shape.FillProperty, Palette.Line);
        toolbarRow.Children.Add(line);
    }

    Border IconButton(string glyph, string label, Action click, bool primary)
    {
        var icon = Glyph(glyph, primary ? Brushes.White : null);
        if (!primary) icon.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        return ToolbarButton(icon, label, click, primary);
    }

    Border ToolbarButton(UIElement content, string label, Action click, bool primary)
    {
        var button = new Border
        {
            Width = Metrics.ButtonSize,
            Height = Metrics.ButtonSize,
            CornerRadius = new CornerRadius(Metrics.ButtonRadius),
            Margin = new Thickness(1, 0, 1, 0),
            Child = content,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = primary ? PrimaryTag : null
        };
        if (primary) button.SetResourceReference(Border.BackgroundProperty, Palette.Accent);
        button.MouseEnter += delegate { Paint(button, true); ShowTooltip(button, label); };
        button.MouseLeave += delegate { Paint(button, false); tooltip.Visibility = Visibility.Hidden; };
        button.MouseLeftButtonUp += delegate { Guarded("Editor button " + label, click); };
        return button;
    }

    // Button for one tool or a group of related tools; a group shows the tool picked last.
    void AddToolGroup(params Tool[] tools)
    {
        var group = new ToolGroup(tools);
        UIElement content = group.IconHost;
        if (tools.Length > 1)
        {
            // small triangle bottom right: there is more
            var withMarker = new Grid { Width = Metrics.ButtonSize, Height = Metrics.ButtonSize };
            withMarker.Children.Add(group.IconHost);
            var triangle = new Shapes.Path
            {
                Data = Geometry.Parse("M 30,26 L 30,30 L 26,30 Z"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            triangle.SetResourceReference(Shapes.Shape.FillProperty, Palette.Muted);
            withMarker.Children.Add(triangle);
            content = withMarker;
        }
        group.Button = ToolbarButton(content, "", delegate
        {
            if (Array.IndexOf(group.Tools, interaction.Tool) >= 0 && group.Tools.Length > 1)
            {
                if (openMenuGroup == group) CloseMenu(); else OpenMenu(group);
            }
            else SetTool(group.Current);
        }, false);
        group.Button.MouseEnter += delegate
        {
            if (openMenuGroup == null) ShowTooltip(group.Button, ToolLabel(group));
            else tooltip.Visibility = Visibility.Hidden;
        };
        ShowIcon(group);
        groups.Add(group);
        toolbarRow.Children.Add(group.Button);
    }

    string ToolLabel(ToolGroup group)
    {
        string label = UiText.Editor.ToolNames[(int)group.Current] + "   " + ToolKeys[(int)group.Current];
        if (group.Tools.Length > 1 && Array.IndexOf(group.Tools, interaction.Tool) >= 0) label += UiText.Editor.MoreHint;
        return label;
    }

    static void ShowIcon(ToolGroup group)
    {
        group.IconHost.Children.Clear();
        group.IconHost.Children.Add(ToolIcon(group.Current, group.IconBrush));
    }

    // Menu of a tool group: icon, name and key, right next to the button.
    void OpenMenu(ToolGroup group)
    {
        tooltip.Visibility = Visibility.Hidden;
        openMenuGroup = group;
        menuItems.Children.Clear();
        foreach (Tool groupTool in group.Tools) menuItems.Children.Add(MenuEntry(groupTool));
        toolMenu.Visibility = Visibility.Visible;
        toolMenu.InvalidateMeasure();   // otherwise the height from the last time sticks
        toolMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point buttonPosition = group.Button.TranslatePoint(new Point(0, 0), root);
        double toolbarTop = Canvas.GetTop(toolbar), menuHeight = toolMenu.DesiredSize.Height;
        Rect monitor = MonitorArea();
        Canvas.SetLeft(toolMenu, Math.Max(monitor.Left + 4, Math.Min(monitor.Right - 4 - toolMenu.DesiredSize.Width, buttonPosition.X - 6)));
        Canvas.SetTop(toolMenu, toolbarTop + toolbar.ActualHeight + 6 + menuHeight < monitor.Bottom - 4 ? toolbarTop + toolbar.ActualHeight + 6 : toolbarTop - 6 - menuHeight);
    }

    Border MenuEntry(Tool entryTool)
    {
        bool active = entryTool == interaction.Tool;
        var icon = new Grid { Width = 26, HorizontalAlignment = HorizontalAlignment.Left };
        icon.Children.Add(ToolIcon(entryTool, new SolidColorBrush(ThemeColor(active ? Palette.Accent : Palette.Text))));
        var key = new TextBlock { Text = ToolKeys[(int)entryTool].ToString(), FontSize = 11.5, FontFamily = Palette.UiFont, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        key.SetResourceReference(TextBlock.ForegroundProperty, Palette.Muted);
        var name = new TextBlock { Text = UiText.Editor.ToolNames[(int)entryTool], FontSize = 12.5, FontFamily = Palette.UiFont, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(key, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(key);
        row.Children.Add(name);
        var entry = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 7, 12, 7), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = active ? ActiveTag : null };
        Paint(entry, false);
        entry.MouseEnter += delegate { Paint(entry, true); };
        entry.MouseLeave += delegate { Paint(entry, false); };
        entry.MouseLeftButtonUp += delegate { Guarded("Choosing a interaction.Tool", () => SetTool(entryTool)); };
        return entry;
    }

    internal void CloseMenu()
    {
        openMenuGroup = null;
        toolMenu.Visibility = Visibility.Hidden;
    }

    /// <summary>Icons: font glyphs where a suitable one exists, otherwise drawn in the same stroke style.</summary>
    internal static UIElement ToolIcon(Tool icon, SolidColorBrush foreground)
    {
        switch (icon)
        {
            case Tool.Move: return Glyph(IconGlyphs.Move, foreground);
            case Tool.Arrow: return Glyph(IconGlyphs.ArrowUpRight, foreground);
            case Tool.Box: return Glyph(IconGlyphs.Rectangle, foreground);
            case Tool.Ellipse: return Glyph(IconGlyphs.Ellipse, foreground);
            case Tool.Pen: return Glyph(IconGlyphs.Pen, foreground);
            case Tool.Marker: return Glyph(IconGlyphs.Highlight, foreground);
            case Tool.Text: return Glyph(IconGlyphs.Text, foreground);
            case Tool.Number: return NumberIcon(foreground);
            case Tool.Pixel: return PixelateIcon(foreground);
        }
        var canvas = new Canvas { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon == Tool.Line)
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 2.5,13.5 L 13.5,2.5"), Stroke = foreground, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        else if (icon == Tool.Spot)
        {
            // dimmed area with a bright hole
            var hole = new EllipseGeometry(new Point(8, 8), 4.5, 4.5);
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Combine(new RectangleGeometry(new Rect(1, 1, 14, 14), 2.5, 2.5), hole, GeometryCombineMode.Exclude, null), Fill = foreground, Opacity = 0.45 });
            canvas.Children.Add(new Shapes.Path { Data = new EllipseGeometry(new Point(8, 8), 4.5, 4.5), Stroke = foreground, StrokeThickness = 1.25 });
        }
        else if (icon == Tool.Label)
        {
            // filled plate with the A punched out
            var plate = new RectangleGeometry(new Rect(0, 1.5, 16, 13), 3, 3);
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Combine(plate, Digits("A", 11, new Point(8, 8)), GeometryCombineMode.Exclude, null), Fill = foreground });
        }
        else if (icon == Tool.Blur)
        {
            // drop, as is common for blur tools
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 8,1.5 C 8,1.5 3,7.4 3,10.3 A 5,5 0 0 0 13,10.3 C 13,7.4 8,1.5 8,1.5 Z"), Stroke = foreground, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Parse("M 5.6,10.6 A 2.6,2.6 0 0 0 7.6,12.7"), Stroke = foreground, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.6 });
        }
        return canvas;
    }

    // Circle and digit as geometry, the digit centered by its visible outline.
    static Grid NumberIcon(SolidColorBrush foreground)
    {
        var icon = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new Shapes.Ellipse { Stroke = foreground, StrokeThickness = 1.5 });
        icon.Children.Add(new Shapes.Path
        {
            Data = Digits("1", 11.5, new Point(9, 9)),
            Fill = foreground,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        return icon;
    }

    // 3x3 squares on whole pixels: 4 wide, 1 gap.
    static Canvas PixelateIcon(SolidColorBrush foreground)
    {
        var icon = new Canvas { Width = 14, Height = 14, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 9; i++)
        {
            var square = new Shapes.Rectangle { Width = 4, Height = 4, Fill = foreground, Opacity = i % 2 == 0 ? 1 : 0.4 };
            Canvas.SetLeft(square, i % 3 * 5);
            Canvas.SetTop(square, i / 3 * 5);
            icon.Children.Add(square);
        }
        return icon;
    }

    // Button background: active = Sel, mouse over = Hover, otherwise transparent.
    static void Paint(Border button, bool hovered)
    {
        string? state = button.Tag as string;
        if (state == PrimaryTag) button.Opacity = hovered ? 0.88 : 1;
        else if (state == ActiveTag) button.SetResourceReference(Border.BackgroundProperty, Palette.Selected);
        else if (hovered) button.SetResourceReference(Border.BackgroundProperty, Palette.Hover);
        else button.Background = Brushes.Transparent;
    }

    Color ThemeColor(string key) => ((SolidColorBrush)Resources[key]).Color;

    internal void SetTool(Tool selected)
    {
        CommitText();
        CloseMenu();
        interaction.Tool = selected;
        foreach (var group in groups)
        {
            bool active = Array.IndexOf(group.Tools, selected) >= 0;
            if (active && group.Current != selected) { group.Current = selected; ShowIcon(group); }
            group.Button.Tag = active ? ActiveTag : null;
            Paint(group.Button, group.Button.IsMouseOver);
            group.IconBrush.Color = ThemeColor(active ? Palette.Accent : Palette.Text);
            if (group.Button.IsMouseOver) ShowTooltip(group.Button, ToolLabel(group));
        }
        if (selected != Tool.Move) document.Selected = null;
        UpdateNumberPreview();
    }

    internal void SetColor(int index)
    {
        style.SetColor(index);
        for (int i = 0; i < colorRings.Count; i++) colorRings[i].Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
        if (textBox != null) StyleTextBox();
        UpdateNumberPreview();
    }

    internal void SetWidth(int index)
    {
        style.SetWidth(index);
        for (int i = 0; i < widthButtons.Count; i++)
        {
            widthButtons[i].Tag = i == index ? ActiveTag : null;
            Paint(widthButtons[i], widthButtons[i].IsMouseOver);
        }
        if (textBox != null) StyleTextBox();
        UpdateNumberPreview();
    }

    // One size step via the mouse wheel or +/-.
    void StepWidth(int direction)
    {
        if (!style.StepWidth(direction)) return;
        SetWidth(style.WidthIndex);
        ShowNotice(UiText.Editor.SizeNote(UiText.Editor.WidthNames[style.WidthIndex]));
    }

    void ShowTooltip(FrameworkElement target, string text)
    {
        tooltipText.Text = text;
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point position = target.TranslatePoint(new Point(0, 0), root);
        double toolbarTop = Canvas.GetTop(toolbar), toolbarHeight = toolbar.ActualHeight, tooltipHeight = tooltip.DesiredSize.Height;
        Rect monitor = MonitorArea();
        Canvas.SetLeft(tooltip, Math.Max(monitor.Left + 4, Math.Min(monitor.Right - 4 - tooltip.DesiredSize.Width, position.X + target.ActualWidth / 2 - tooltip.DesiredSize.Width / 2)));
        Canvas.SetTop(tooltip, toolbarTop + toolbarHeight + 8 + tooltipHeight < monitor.Bottom - 4 ? toolbarTop + toolbarHeight + 8 : toolbarTop - 8 - tooltipHeight);
        tooltip.Visibility = Visibility.Visible;
    }

    void ShowNotice(string text)
    {
        noticeText.Text = text;
        notice.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double toolbarTop = Canvas.GetTop(toolbar), noticeHeight = notice.DesiredSize.Height;
        Rect monitor = MonitorArea();
        Canvas.SetLeft(notice, Canvas.GetLeft(toolbar) + (toolbar.ActualWidth - notice.DesiredSize.Width) / 2);
        Canvas.SetTop(notice, toolbarTop - 12 - noticeHeight > monitor.Top + 4 ? toolbarTop - 12 - noticeHeight : toolbarTop + toolbar.ActualHeight + 12);
        notice.Visibility = Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    // ---------- For the pixel tests ----------

    internal Canvas Surface => root;
    internal FrameworkElement Toolbar => toolbar;
    internal FrameworkElement ToolMenu => toolMenu;
    internal void OpenGroupMenu(int groupIndex) => OpenMenu(groups[groupIndex]);
    internal void TypeText(string text) => textBox!.Text = text;
}
