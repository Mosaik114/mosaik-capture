namespace MosaikCapture.Engine.Tests.Screenshot;

// The editor's document model: items, order, moving, deleting, crop, text and undo/redo, plus the pen-hold snap.
public sealed class EditorDocumentTests
{
    static readonly Rect StartCrop = new(10, 10, 500, 300);

    readonly EditorDocument document = new();
    readonly List<string> events = new();

    public EditorDocumentTests()
    {
        document.Reset(StartCrop);
        document.Inserted += (item, index) => events.Add("+" + item.Kind + "@" + index);
        document.Removed += (item, index) => events.Add("-" + item.Kind + "@" + index);
        document.Moved += item => events.Add("move " + item.Kind + " " + item.Offset);
        document.CropChanged += () => events.Add("crop " + document.Crop);
    }

    static MarkupItem Item(MarkupKind kind, double x = 0, double y = 0) => new(kind, new Rect(x, y, 20, 10));

    static string Kinds(EditorDocument document) => string.Join(",", document.Items.Select(item => item.Kind));

    [Fact]
    public void CoversGoToTheBottomEverythingElseOnTop()
    {
        document.Add(Item(MarkupKind.Shape));
        document.Add(Item(MarkupKind.Cover));
        document.Add(Item(MarkupKind.Text));
        document.Add(Item(MarkupKind.Cover));

        Assert.Equal("Cover,Cover,Shape,Text", Kinds(document));
        Assert.Equal(2, document.CoverCount);
        Assert.Equal(new[] { "+Shape@0", "+Cover@0", "+Text@2", "+Cover@0" }, events);
    }

    [Fact]
    public void UndoAndRedoReplayAddsInOrder()
    {
        document.Add(Item(MarkupKind.Shape));
        document.Add(Item(MarkupKind.Number));

        Assert.True(document.Undo());
        Assert.Equal("Shape", Kinds(document));
        Assert.True(document.Undo());
        Assert.Empty(document.Items);
        Assert.False(document.Undo());
        Assert.True(document.Redo());
        Assert.True(document.Redo());
        Assert.False(document.Redo());

        Assert.Equal("Shape,Number", Kinds(document));
        Assert.True(document.HasChanges);
    }

    [Fact]
    public void NumbersCountTheBadgesThatAreThere()
    {
        Assert.Equal(1, document.NextNumber);
        document.Add(Item(MarkupKind.Number));
        document.Add(Item(MarkupKind.Shape));
        document.Add(Item(MarkupKind.Number));
        Assert.Equal(3, document.NextNumber);

        document.Undo();
        document.Undo();
        Assert.Equal(2, document.NextNumber);
    }

    [Fact]
    public void MovingIsOneUndoableStep()
    {
        var item = Item(MarkupKind.Shape);
        document.Add(item);
        var start = item.Offset;

        document.MoveTo(item, new Vector(5, 0));
        document.MoveTo(item, new Vector(30, 12));
        document.CommitMove(item, start);
        Assert.Equal(new Rect(30, 12, 20, 10), item.Bounds);

        document.Undo();
        Assert.Equal(new Vector(0, 0), item.Offset);
        document.Redo();
        Assert.Equal(new Vector(30, 12), item.Offset);
        document.Undo();
        document.Undo();   // and the add before it
        Assert.Empty(document.Items);
    }

    [Fact]
    public void AMoveThatEndsWhereItStartedIsNoChange()
    {
        var item = Item(MarkupKind.Shape);
        document.Add(item);
        document.MoveTo(item, new Vector(9, 9));
        document.MoveTo(item, new Vector(0, 0));

        document.CommitMove(item, new Vector(0, 0));

        document.Undo();   // removes the add, there is no move step
        Assert.Empty(document.Items);
    }

    [Fact]
    public void DeletingTheSelectionComesBackAtItsPlace()
    {
        var first = Item(MarkupKind.Shape);
        var middle = Item(MarkupKind.Highlight);
        var last = Item(MarkupKind.Text);
        document.Add(first);
        document.Add(middle);
        document.Add(last);
        document.Selected = middle;

        Assert.True(document.DeleteSelected());
        Assert.Null(document.Selected);
        Assert.Equal("Shape,Text", Kinds(document));

        document.Undo();
        Assert.Equal("Shape,Highlight,Text", Kinds(document));
        document.Redo();
        Assert.Equal("Shape,Text", Kinds(document));
        Assert.False(document.DeleteSelected());   // nothing selected any more
    }

    [Fact]
    public void UndoingTheAddOfTheSelectedItemClearsTheSelection()
    {
        var item = Item(MarkupKind.Number);
        document.Add(item);
        document.Selected = item;

        document.Undo();

        Assert.Null(document.Selected);
    }

    [Fact]
    public void SpotlightHolesFollowTheirItems()
    {
        var spot = new MarkupItem(MarkupKind.Spotlight, new Rect(100, 100, 50, 40));
        document.Add(spot);
        document.Add(Item(MarkupKind.Shape));

        document.MoveTo(spot, new Vector(10, -20));

        Assert.Equal(new[] { new Rect(110, 80, 50, 40) }, document.SpotlightHoles);
    }

    [Fact]
    public void CropChangesAreOneUndoableStepAndMixWithItems()
    {
        var before = document.Crop;
        document.SetCrop(new Rect(20, 20, 400, 200));
        document.SetCrop(new Rect(30, 30, 300, 100));
        document.CommitCrop(before);
        document.Add(Item(MarkupKind.Shape));

        document.Undo();   // the item
        Assert.Equal(new Rect(30, 30, 300, 100), document.Crop);
        document.Undo();   // the crop
        Assert.Equal(StartCrop, document.Crop);
        document.Redo();
        Assert.Equal(new Rect(30, 30, 300, 100), document.Crop);
        Assert.Contains("crop " + StartCrop, events);
    }

    [Fact]
    public void UnchangedCropIsNoStep()
    {
        document.SetCrop(new Rect(0, 0, 1, 1));
        document.SetCrop(StartCrop);

        document.CommitCrop(StartCrop);

        Assert.False(document.HasChanges);
    }

    [Fact]
    public void TextIsKeptOnlyWithVisibleCharacters()
    {
        document.BeginText(new Point(5, 5), plate: true);
        Assert.True(document.Typing);
        Assert.True(document.Draft!.Plate);

        Assert.True(document.EndText("Hallo"));
        Assert.False(document.Typing);

        document.BeginText(new Point(5, 5), plate: false);
        Assert.False(document.EndText("  \r\n "));   // cancelled: nothing is added
        Assert.False(document.EndText("late"));      // no draft any more
        Assert.False(document.HasChanges);
    }

    [Fact]
    public void ResetStartsOver()
    {
        document.Add(Item(MarkupKind.Shape));
        document.BeginText(new Point(1, 1), false);
        document.Selected = document.Items[0];

        document.Reset(new Rect(0, 0, 10, 10));

        Assert.Empty(document.Items);
        Assert.Null(document.Selected);
        Assert.False(document.Typing);
        Assert.False(document.HasChanges);
        Assert.Equal(new Rect(0, 0, 10, 10), document.Crop);
    }

    // ---------- Pen-hold snapping ----------

    [Fact]
    public void HeldPenSnapsOnceAndClosedShapesGrowAroundTheirCenter() => WpfThread.Run(() =>
    {
        var snap = new PenSnap();
        var circle = Strokes.Ellipse(new Point(100, 100), 40, 40, 0.1);

        Assert.True(snap.TrySnap(circle, marker: false, width: 3, hold: new Point(140, 100)));
        Assert.True(snap.Snapped);
        Assert.False(snap.Open);
        Assert.False(snap.TrySnap(circle, false, 3, new Point(140, 100)));   // only once per stroke

        Rect start = snap.Initial(false, 3, new Point(140, 100)).Bounds;
        Rect bigger = snap.Follow(new Point(180, 100), false, 3).Bounds;
        Assert.InRange(bigger.Width / start.Width, 1.9, 2.1);
        Assert.Equal(start.X + start.Width / 2, bigger.X + bigger.Width / 2, 1);   // scaled around the stroke's center

        snap.Reset();
        Assert.False(snap.Snapped);
    });

    [Fact]
    public void HeldHighlighterBecomesALevelLineThatFollowsThePointer() => WpfThread.Run(() =>
    {
        var snap = new PenSnap();
        var stroke = Strokes.Line(new Point(10, 50), new Point(120, 56), 30);

        Assert.True(snap.TrySnap(stroke, marker: true, width: 3, hold: new Point(120, 56)));
        Assert.True(snap.Open);

        var line = Assert.IsType<LineGeometry>(snap.Follow(new Point(200, 58), true, 3));
        Assert.Equal(new Point(10, 50), line.StartPoint);
        Assert.Equal(new Point(200, 50), line.EndPoint);   // nearly level: exactly level
    });

    [Fact]
    public void ScribblesAndTinyStrokesDoNotSnap() => WpfThread.Run(() =>
    {
        var snap = new PenSnap();

        Assert.False(snap.TrySnap(Strokes.Line(new Point(0, 0), new Point(5, 3), 5), false, 3, new Point(5, 3)));
        var scribble = Enumerable.Range(0, 90).Select(i => new Point(90 + 60 * Math.Sin(i * 0.37) * Math.Cos(i * 0.05), 80 + 50 * Math.Sin(i * 0.61 + 1))).ToList();
        Assert.False(snap.TrySnap(scribble, false, 3, scribble[scribble.Count - 1]));
        Assert.False(snap.Snapped);
    });

    [Fact]
    public void HeldArrowKeepsItsStartAndFillsItsHead() => WpfThread.Run(() =>
    {
        var snap = new PenSnap();
        var stroke = Strokes.Line(new Point(10, 80), new Point(160, 80), 50);
        stroke.AddRange(Strokes.Line(new Point(160, 80), new Point(135, 60), 8));

        Assert.True(snap.TrySnap(stroke, false, 3, stroke[stroke.Count - 1]));
        Assert.True(snap.Arrow);
        var arrow = Assert.IsType<PathGeometry>(snap.Follow(new Point(300, 200), false, 3));
        Assert.Equal(new Point(10, 80), arrow.Figures[0].StartPoint);
        Assert.Equal(new Point(300, 200), arrow.Figures[1].StartPoint);
    });
}
