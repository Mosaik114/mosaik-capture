namespace MosaikCapture.Engine.Screenshot;

/// <summary>What a markup item is; decides its layer and how it reacts.</summary>
internal enum MarkupKind
{
    /// <summary>Arrow, box, ellipse, line, pen stroke or a snapped shape.</summary>
    Shape,
    /// <summary>Highlighter stroke or a highlight snapped onto a text line.</summary>
    Highlight,
    /// <summary>A spotlight: everything outside all spotlights is dimmed.</summary>
    Spotlight,
    /// <summary>Pixelated or blurred area: part of the image, always below everything else.</summary>
    Cover,
    /// <summary>A numbered badge; numbers count up.</summary>
    Number,
    /// <summary>Text, with or without a plate.</summary>
    Text,
}

/// <summary>One piece of markup in the editor's document. The window draws it; the document owns order and position.</summary>
internal sealed class MarkupItem
{
    public MarkupItem(MarkupKind kind, Rect area)
    {
        Kind = kind;
        Area = area;
    }

    public MarkupKind Kind { get; }

    /// <summary>Where it was placed (WPF units).</summary>
    public Rect Area { get; }

    /// <summary>How far it was moved since (move tool).</summary>
    public Vector Offset { get; internal set; }

    /// <summary>Where it is now.</summary>
    public Rect Bounds => Rect.Offset(Area, Offset);
}

/// <summary>A text being typed: where it started and whether it has a plate.</summary>
internal sealed class TextDraft
{
    public TextDraft(Point position, bool plate)
    {
        Position = position;
        Plate = plate;
    }

    public Point Position { get; }
    public bool Plate { get; }
}

/// <summary>
/// The screenshot editor's document: the markup items in drawing order, the crop, the selection and a text
/// being typed. Every change goes through here and is undoable; undo and redo replay changes of the model,
/// and the window follows through the events (it only draws and routes input). Plain model, no UI.
/// </summary>
internal sealed class EditorDocument
{
    readonly List<MarkupItem> items = new();
    readonly UndoHistory history = new();

    /// <summary>The items from bottom to top.</summary>
    public IReadOnlyList<MarkupItem> Items => items;

    /// <summary>The visible part of the screenshot (WPF units).</summary>
    public Rect Crop { get; private set; }

    /// <summary>The item the move tool picked last (Del removes it).</summary>
    public MarkupItem? Selected { get; set; }

    /// <summary>The text being typed, or null.</summary>
    public TextDraft? Draft { get; private set; }

    public bool Typing => Draft != null;

    /// <summary>Anything to lose when cancelling.</summary>
    public bool HasChanges => history.HasChanges;

    /// <summary>The number the next badge shows.</summary>
    public int NextNumber => items.Count(item => item.Kind == MarkupKind.Number) + 1;

    /// <summary>Covers lie at the bottom; the spotlight dimming goes right above them.</summary>
    public int CoverCount => items.Count(item => item.Kind == MarkupKind.Cover);

    /// <summary>The holes of the spotlight dimming, where the spotlights are now.</summary>
    public IEnumerable<Rect> SpotlightHoles => items.Where(item => item.Kind == MarkupKind.Spotlight).Select(item => item.Bounds);

    /// <summary>An item was inserted at this index of <see cref="Items"/>.</summary>
    public event Action<MarkupItem, int>? Inserted;
    /// <summary>An item was removed (it was at this index).</summary>
    public event Action<MarkupItem, int>? Removed;
    /// <summary>An item's offset changed.</summary>
    public event Action<MarkupItem>? Moved;
    public event Action? CropChanged;

    /// <summary>A fresh document for a new screenshot (no events: the window starts over as well).</summary>
    public void Reset(Rect crop)
    {
        items.Clear();
        history.Clear();
        Selected = null;
        Draft = null;
        Crop = crop;
    }

    // ---------- Items ----------

    /// <summary>Adds an item: covers at the very bottom (they belong to the image), everything else on top.</summary>
    public void Add(MarkupItem item)
    {
        Insert(item);
        history.Push(() => Remove(item), () => Insert(item));
    }

    /// <summary>Removes the selected item (undoable: it comes back at its place).</summary>
    public bool DeleteSelected()
    {
        var item = Selected;
        if (item == null) return false;
        int index = items.IndexOf(item);
        if (index < 0) return false;
        Remove(item);
        history.Push(() => InsertAt(item, Math.Min(index, items.Count)), () => Remove(item));
        return true;
    }

    /// <summary>Moves an item while dragging (recorded as one change by <see cref="CommitMove"/>).</summary>
    public void MoveTo(MarkupItem item, Vector offset)
    {
        item.Offset = offset;
        Moved?.Invoke(item);
    }

    /// <summary>Ends a drag that started at <paramref name="startOffset"/>; records it if the item moved.</summary>
    public void CommitMove(MarkupItem item, Vector startOffset)
    {
        var endOffset = item.Offset;
        if (endOffset != startOffset) history.Push(() => MoveTo(item, startOffset), () => MoveTo(item, endOffset));
    }

    // ---------- Crop ----------

    /// <summary>Changes the crop while dragging (recorded as one change by <see cref="CommitCrop"/>).</summary>
    public void SetCrop(Rect crop)
    {
        Crop = crop;
        CropChanged?.Invoke();
    }

    /// <summary>Ends a crop drag that started at <paramref name="before"/>; records it if the crop changed.</summary>
    public void CommitCrop(Rect before)
    {
        var after = Crop;
        if (before != after) history.Push(() => SetCrop(before), () => SetCrop(after));
    }

    // ---------- Text ----------

    /// <summary>Starts typing a text at <paramref name="position"/>.</summary>
    public void BeginText(Point position, bool plate) => Draft = new TextDraft(position, plate);

    /// <summary>
    /// Ends typing. True if the text has visible characters and is to be kept (then the caller adds it as a
    /// <see cref="MarkupKind.Text"/> item); an empty text is dropped, which is also how typing is cancelled.
    /// </summary>
    public bool EndText(string text)
    {
        if (Draft == null) return false;
        Draft = null;
        return text.Trim().Length > 0;
    }

    // ---------- Undo ----------

    public bool Undo() => history.Undo();

    public bool Redo() => history.Redo();

    void Insert(MarkupItem item) => InsertAt(item, item.Kind == MarkupKind.Cover ? 0 : items.Count);

    void InsertAt(MarkupItem item, int index)
    {
        items.Insert(index, item);
        Inserted?.Invoke(item, index);
    }

    void Remove(MarkupItem item)
    {
        int index = items.IndexOf(item);
        if (index < 0) return;
        items.RemoveAt(index);
        if (Selected == item) Selected = null;
        Removed?.Invoke(item, index);
    }
}

/// <summary>
/// A pen or highlighter stroke held still snaps to a clean shape that then follows the pointer: open
/// shapes (line, arrow) keep their start and move their end, closed shapes grow around their center.
/// Plain model of that lifecycle; the window only shows the geometry.
/// </summary>
internal sealed class PenSnap
{
    /// <summary>Strokes smaller than this stay freehand when held.</summary>
    const double MinSnapSize = 12;
    /// <summary>Closed shapes grow with the pointer's distance from their center, at least this distance.</summary>
    const double MinSnapReach = 8;
    /// <summary>The highlighter levels out below this slope (about 8°).</summary>
    const double MarkerLevelSine = 0.14;
    /// <summary>A closed shape never shrinks below this share.</summary>
    const double MinScale = 0.1;

    Geometry? shape;
    Point start, center;
    double reach;

    /// <summary>The stroke has snapped; it now follows the pointer as a clean shape.</summary>
    public bool Snapped { get; private set; }

    /// <summary>Line or arrow: the end follows the pointer.</summary>
    public bool Open { get; private set; }

    /// <summary>An arrow: its head is filled.</summary>
    public bool Arrow { get; private set; }

    /// <summary>A new stroke starts.</summary>
    public void Reset() => Snapped = false;

    /// <summary>
    /// Tries to snap the stroke <paramref name="points"/> held still at <paramref name="hold"/>; true if it did.
    /// The highlighter always becomes a straight line; the pen becomes whatever <see cref="ShapeRecognizer"/> sees.
    /// </summary>
    public bool TrySnap(List<Point> points, bool marker, double width, Point hold)
    {
        if (Snapped || points.Count == 0) return false;
        Rect bounds = ShapeRecognizer.Bounds(points);
        if (Math.Max(bounds.Width, bounds.Height) < MinSnapSize) return false;
        ShapeKind kind;
        if (marker)
        {
            kind = ShapeKind.Line;   // highlighter: straight line, nearly horizontal becomes exactly horizontal
            shape = null;
        }
        else
        {
            shape = ShapeRecognizer.Recognize(points, width, out kind);
            if (shape == null) return false;
        }
        Snapped = true;
        Open = kind is ShapeKind.Line or ShapeKind.Arrow;
        Arrow = kind == ShapeKind.Arrow;
        start = points[0];
        center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        reach = Math.Max(MinSnapReach, (hold - center).Length);
        if (Open && shape != null)
            start = shape is PathGeometry path && path.Figures.Count > 0 ? path.Figures[0].StartPoint : ((LineGeometry)shape).StartPoint;
        return true;
    }

    /// <summary>The geometry right after snapping, with the pointer still at <paramref name="hold"/>.</summary>
    public Geometry Initial(bool marker, double width, Point hold) =>
        Open ? Follow(marker || shape == null ? hold : LineEnd(shape), marker, width) : shape!;

    /// <summary>The snapped shape for the pointer at <paramref name="pointer"/>.</summary>
    public Geometry Follow(Point pointer, bool marker, double width)
    {
        if (Open)
        {
            double angle = Math.Atan2(pointer.Y - start.Y, pointer.X - start.X) * 180 / Math.PI;
            bool level = marker ? Math.Abs(Math.Sin(angle * Math.PI / 180)) < MarkerLevelSine : ShapeRecognizer.NearAxis(angle, ShapeRecognizer.AxisSnapDegrees);
            if (level) pointer = ShapeRecognizer.SnapAxis(start, pointer);
            return Arrow ? ShapeRecognizer.Arrow(start, pointer, width) : new LineGeometry(start, pointer);
        }
        double factor = Math.Max(MinScale, (pointer - center).Length / reach);
        var scaled = shape!.Clone();
        scaled.Transform = new ScaleTransform(factor, factor, center.X, center.Y);
        return scaled;
    }

    static Point LineEnd(Geometry geometry) =>
        geometry is LineGeometry line ? line.EndPoint : ((PathGeometry)geometry).Figures[1].StartPoint;   // arrow: tip
}
