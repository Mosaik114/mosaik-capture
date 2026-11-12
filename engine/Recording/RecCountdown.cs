namespace MosaikCapture.Engine.Recording;

// 3-2-1 countdown before recording starts (Esc cancels).
class RecCountdown : Window
{
    const double D = 132, Ring = 3, Pad = 24;
    readonly IntPtr hwnd;
    readonly TextBlock num;
    readonly Shapes.Path arc;
    readonly ScaleTransform pop = new ScaleTransform(1, 1);
    readonly DispatcherTimer tick;
    Action? launch, done, abort;
    long t0;
    int shown;
    bool launched;

    public RecCountdown()
    {
        RecUi.Setup(this, "Mosaiks-Aufnahme Countdown");
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var circle = new Grid { Width = D, Height = D };
        var back = new Shapes.Ellipse { StrokeThickness = 1, Effect = RecUi.Shadow(28, 8, 0.35) };
        back.SetResourceReference(Shapes.Shape.FillProperty, "Bg");
        back.SetResourceReference(Shapes.Shape.StrokeProperty, "Line");
        circle.Children.Add(back);
        var rail = new Shapes.Ellipse { Margin = new Thickness(10 - Ring / 2), StrokeThickness = Ring };
        rail.SetResourceReference(Shapes.Shape.StrokeProperty, "Line");
        circle.Children.Add(rail);
        arc = new Shapes.Path { StrokeThickness = Ring, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
        circle.Children.Add(arc);
        num = new TextBlock
        {
            FontFamily = new FontFamily("Georgia"),
            FontSize = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            RenderTransform = pop,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        num.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        circle.Children.Add(num);

        var hint = RecUi.Text(12, "Muted");
        hint.Text = "Esc = abbrechen";
        var chip = ShotOverlay.Chip(hint);
        chip.HorizontalAlignment = HorizontalAlignment.Center;
        chip.Margin = new Thickness(0, 12, 0, 0);
        chip.Effect = RecUi.Shadow(14, 3, 0.3);

        var st = new StackPanel { Margin = new Thickness(Pad) };
        st.Children.Add(circle);
        st.Children.Add(chip);
        Content = st;

        hwnd = RecUi.Handle(this, RecUi.NoActivate | RecUi.Tool | RecUi.ClickThrough);
        RecUi.Exclude(hwnd);
        tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        tick.Tick += delegate { Step(); };
    }

    int duration = 3;

    public void Run(Int32Rect r, ShotOverlay.RECT mon, Action onLaunch, Action onDone, Action onAbort)
    {
        launch = onLaunch;
        done = onDone;
        abort = onAbort;
        launched = false;
        shown = 0;
        Palette.Apply(Resources);
        duration = CaptureSettings.Countdown;
        if (duration == 0) { launch(); done(); return; }
        Show(duration, 0);
        Left = -10000;
        Show();
        UpdateLayout();
        double s = RecUi.Scale(hwnd);
        int pad = (int)Math.Round(Pad * s), d = (int)Math.Round(D * s), w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        int x = Math.Max(mon.left - pad, Math.Min(mon.right - w + pad, cx - w / 2));
        int y = Math.Max(mon.top - pad, Math.Min(mon.bottom - h + pad, cy - pad - d / 2));
        RecUi.Move(hwnd, x, y);
        t0 = Stopwatch.GetTimestamp();
        tick.Start();
    }

    public void Stop()
    {
        tick.Stop();
        Hide();
    }

    void Step()
    {
        double e = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
        if (RecUi.KeyDown(0x1B)) { Stop(); abort!(); return; }
        if (!launched && e >= duration - 0.25) { launched = true; launch!(); }
        if (e >= duration) { Stop(); done!(); return; }
        Show(duration - (int)e, 1 - (e - Math.Floor(e)));
    }

    // Number (with a small pop on change) and an arc that runs down once per second.
    void Show(int n, double left)
    {
        if (n != shown)
        {
            shown = n;
            num.Text = n.ToString(CultureInfo.CurrentCulture);
            var d = new Duration(TimeSpan.FromMilliseconds(260));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.28, 1, d) { EasingFunction = ease });
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.28, 1, d) { EasingFunction = ease });
            num.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, d) { EasingFunction = ease });
        }
        double r = D / 2 - 10, c = D / 2;
        if (left >= 0.999) { arc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
        if (left <= 0.001) { arc.Data = null; return; }
        double a = left * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, left > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        arc.Data = g;
    }
}
