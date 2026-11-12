namespace MosaikCapture.Engine.Tests.Screenshot;

// The editor's tool and pointer-gesture rules, without a window.
public sealed class EditorInteractionTests
{
    static EditorInteraction With(ScreenshotEditor.Tool tool) => new() { Tool = tool };

    [Fact]
    public void AnOpenMenuOrTextComesFirst()
    {
        var interaction = With(ScreenshotEditor.Tool.Pen);

        Assert.Equal(PressAction.CloseMenu, interaction.Press(menuOpen: true, typing: true, onItem: false, inCrop: true));
        Assert.Equal(PressAction.CommitText, interaction.Press(menuOpen: false, typing: true, onItem: false, inCrop: true));
    }

    [Theory]
    [InlineData(true, true, "MoveItem")]
    [InlineData(true, false, "MoveItem")]     // items may hang over the crop edge
    [InlineData(false, true, "MoveCrop")]
    [InlineData(false, false, "Ignore")]
    public void MoveToolDragsItemsOrTheCrop(bool onItem, bool inCrop, string expected) =>
        Assert.Equal(expected, With(ScreenshotEditor.Tool.Move).Press(false, false, onItem, inCrop).ToString());

    [Theory]
    [InlineData("Text", "StartText")]
    [InlineData("Label", "StartText")]
    [InlineData("Number", "PlaceNumber")]
    [InlineData("Pen", "StartStroke")]
    [InlineData("Arrow", "StartStroke")]
    [InlineData("Blur", "StartStroke")]
    public void DrawingToolsActInsideTheCropOnly(string tool, string expected)
    {
        var interaction = With((ScreenshotEditor.Tool)Enum.Parse(typeof(ScreenshotEditor.Tool), tool));

        Assert.Equal(expected, interaction.Press(false, false, onItem: true, inCrop: true).ToString());
        Assert.Equal(PressAction.Ignore, interaction.Press(false, false, onItem: true, inCrop: false));
    }

    [Fact]
    public void GestureRunsFromBeginToFinish()
    {
        var interaction = With(ScreenshotEditor.Tool.Marker);
        Assert.True(interaction.Idle);
        Assert.False(interaction.CanSnap);

        interaction.Begin(PointerMode.Draw);
        Assert.False(interaction.Idle);
        Assert.True(interaction.CanSnap);

        Assert.Equal(PointerMode.Draw, interaction.Finish());
        Assert.True(interaction.Idle);
        Assert.Equal(PointerMode.None, interaction.Finish());
    }

    [Theory]
    [InlineData("Pen", true)]
    [InlineData("Marker", true)]
    [InlineData("Box", false)]
    [InlineData("Arrow", false)]
    public void OnlyFreehandStrokesSnapToShapes(string tool, bool snaps)
    {
        var interaction = With((ScreenshotEditor.Tool)Enum.Parse(typeof(ScreenshotEditor.Tool), tool));
        interaction.Begin(PointerMode.Draw);

        Assert.Equal(snaps, interaction.CanSnap);
    }

    [Fact]
    public void EscWithChangesAsksOnceThenDiscards()
    {
        var interaction = new EditorInteraction();

        Assert.False(interaction.ConfirmDiscard(hasChanges: false));   // nothing to lose: close at once
        Assert.True(interaction.ConfirmDiscard(hasChanges: true));     // ask
        Assert.True(interaction.DiscardArmed);
        Assert.False(interaction.ConfirmDiscard(hasChanges: true));    // asked already: close

        interaction.DisarmDiscard();
        Assert.True(interaction.ConfirmDiscard(hasChanges: true));     // after anything else it asks again
    }

    [Fact]
    public void GestureStartIsKeptForItsUndoStep()
    {
        var interaction = new EditorInteraction { PressPoint = new Point(5, 6), CropAtPress = new Rect(1, 2, 3, 4), Handle = 7, ItemStart = new Vector(9, 9) };

        interaction.Begin(PointerMode.ResizeCrop);

        Assert.Equal(new Point(5, 6), interaction.PressPoint);
        Assert.Equal(new Rect(1, 2, 3, 4), interaction.CropAtPress);
        Assert.Equal(7, interaction.Handle);
        Assert.Equal(new Vector(9, 9), interaction.ItemStart);
    }

    [Fact]
    public void MovingIsNoDrawing()
    {
        var interaction = With(ScreenshotEditor.Tool.Pen);
        interaction.Begin(PointerMode.MoveCrop);

        Assert.False(interaction.CanSnap);
        interaction.Reset();
        Assert.True(interaction.Idle);
    }
}
