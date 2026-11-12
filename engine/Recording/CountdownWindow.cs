namespace MosaikCapture.Engine.Recording;

/// <summary>3-2-1 countdown before the recording starts (Esc cancels).</summary>
internal sealed class CountdownWindow : Window
{
    const double CircleSize = 132, RingWidth = 3, ShadowRoom = 24;
    const double LaunchLead = 0.25;   // seconds before zero at which ffmpeg is started
    readonly IntPtr hwnd;
    readonly TextBlock number;
    readonly Shapes.Path arc;
    readonly ScaleTransform pop = new(1, 1);
    readonly DispatcherTimer timer;
    Action? launch, finished, cancelled;
    long startTimestamp;
    int shownNumber;
    bool launched;
    int duration = 3;

    public CountdownWindow()
    {
        ToolWindows.Setup(this, UiText.Titles.Countdown);
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        number = new TextBlock
        {
            FontFamily = Palette.TitleFont,
            FontSize = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            RenderTransform = pop,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, Palette.Text);
        arc = new Shapes.Path { StrokeThickness = RingWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Accent);

        var layout = new StackPanel { Margin = new Thickness(ShadowRoom) };
        layout.Children.Add(BuildCircle());
        layout.Children.Add(BuildHint());
        Content = layout;

        hwnd = ToolWindows.Handle(this, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        ToolWindows.ExcludeFromCapture(hwnd);
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += delegate { OnTick(); };
    }

    Grid BuildCircle()
    {
        var circle = new Grid { Width = CircleSize, Height = CircleSize };
        var background = new Shapes.Ellipse { StrokeThickness = 1, Effect = ToolWindows.Shadow(28, 8, 0.35) };
        background.SetResourceReference(Shapes.Shape.FillProperty, Palette.Background);
        background.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Line);
        circle.Children.Add(background);
        var rail = new Shapes.Ellipse { Margin = new Thickness(10 - RingWidth / 2), StrokeThickness = RingWidth };
        rail.SetResourceReference(Shapes.Shape.StrokeProperty, Palette.Line);
        circle.Children.Add(rail);
        circle.Children.Add(arc);
        circle.Children.Add(number);
        return circle;
    }

    static Border BuildHint()
    {
        var hint = ToolWindows.Text(12, Palette.Muted);
        hint.Text = UiText.Recording.CountdownHint;
        var chip = ToolWindows.Chip(hint);
        chip.HorizontalAlignment = HorizontalAlignment.Center;
        chip.Margin = new Thickness(0, 12, 0, 0);
        chip.Effect = ToolWindows.Shadow(14, 3, 0.3);
        return chip;
    }

    /// <summary>
    /// Counts down the configured seconds centered on <paramref name="area"/>. With a countdown of 0
    /// the callbacks run right away and nothing is shown.
    /// </summary>
    public void Run(Int32Rect area, RECT monitor, Action onLaunch, Action onFinished, Action onCancelled)
    {
        launch = onLaunch;
        finished = onFinished;
        cancelled = onCancelled;
        launched = false;
        shownNumber = 0;
        Palette.Apply(Resources);
        duration = CaptureSettings.Countdown;
        if (duration == 0) { onLaunch(); onFinished(); return; }
        ShowFrame(duration, 0);
        Left = -10000;
        Show();
        UpdateLayout();
        PlaceOver(area, monitor);
        startTimestamp = Stopwatch.GetTimestamp();
        timer.Start();
    }

    // Centered on the area, kept on the monitor (the shadow room may hang over the edge).
    void PlaceOver(Int32Rect area, RECT monitor)
    {
        double scale = ToolWindows.Scale(hwnd);
        int shadow = (int)Math.Round(ShadowRoom * scale), circle = (int)Math.Round(CircleSize * scale);
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        int centerX = area.X + area.Width / 2, centerY = area.Y + area.Height / 2;
        int x = Math.Max(monitor.left - shadow, Math.Min(monitor.right - width + shadow, centerX - width / 2));
        int y = Math.Max(monitor.top - shadow, Math.Min(monitor.bottom - height + shadow, centerY - shadow - circle / 2));
        NativeMethods.MoveTopmost(hwnd, x, y);
    }

    public void Stop()
    {
        timer.Stop();
        Hide();
    }

    void OnTick()
    {
        double elapsed = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        if (ToolWindows.IsKeyDown(NativeMethods.VK_ESCAPE)) { Stop(); cancelled!(); return; }
        if (!launched && elapsed >= duration - LaunchLead) { launched = true; launch!(); }
        if (elapsed >= duration) { Stop(); finished!(); return; }
        ShowFrame(duration - (int)elapsed, 1 - (elapsed - Math.Floor(elapsed)));
    }

    /// <summary>The number (with a small pop on change) and an arc that runs down once per second.</summary>
    internal void ShowFrame(int secondsLeft, double fractionOfSecondLeft)
    {
        if (secondsLeft != shownNumber)
        {
            shownNumber = secondsLeft;
            number.Text = secondsLeft.ToString(CultureInfo.InvariantCulture);
            var pulse = new Duration(TimeSpan.FromMilliseconds(260));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.28, 1, pulse) { EasingFunction = ease });
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.28, 1, pulse) { EasingFunction = ease });
            number.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, pulse) { EasingFunction = ease });
        }
        double radius = CircleSize / 2 - 10, center = CircleSize / 2;
        if (fractionOfSecondLeft >= 0.999) { arc.Data = new EllipseGeometry(new Point(center, center), radius, radius); return; }
        if (fractionOfSecondLeft <= 0.001) { arc.Data = null; return; }
        double angle = fractionOfSecondLeft * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(center, center - radius) };
        figure.Segments.Add(new ArcSegment(new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
            new Size(radius, radius), 0, fractionOfSecondLeft > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        arc.Data = geometry;
    }
}
