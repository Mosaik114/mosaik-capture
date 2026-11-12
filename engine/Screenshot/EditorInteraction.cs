namespace MosaikCapture.Engine.Screenshot;

/// <summary>What the pointer is doing in the editor between press and release.</summary>
internal enum PointerMode
{
    None,
    /// <summary>Drawing a stroke, shape or effect area with the current tool.</summary>
    Draw,
    /// <summary>Dragging a markup item (move tool).</summary>
    MoveItem,
    /// <summary>Dragging the whole crop (move tool inside the crop).</summary>
    MoveCrop,
    /// <summary>Dragging one of the eight crop handles.</summary>
    ResizeCrop,
}

/// <summary>What a press of the left button does, decided from the editor's state.</summary>
internal enum PressAction
{
    /// <summary>A tool menu is open: the press only closes it.</summary>
    CloseMenu,
    /// <summary>A text is being typed: the press only finishes it.</summary>
    CommitText,
    MoveItem,
    MoveCrop,
    StartText,
    PlaceNumber,
    StartStroke,
    /// <summary>Outside the crop with a drawing tool: nothing happens.</summary>
    Ignore,
}

/// <summary>
/// The editor's interaction state: the active tool and the pointer gesture in progress. A plain model, so
/// the rules (what a press does, which gestures may snap or change the width) are testable without a window.
/// </summary>
internal sealed class EditorInteraction
{
    public ScreenshotEditor.Tool Tool { get; set; } = ScreenshotEditor.Tool.Arrow;

    public PointerMode Mode { get; private set; }

    /// <summary>No gesture in progress (keys and the mouse wheel act only then).</summary>
    public bool Idle => Mode == PointerMode.None;

    /// <summary>Pen and highlighter draw freehand and can snap to a shape when held still.</summary>
    public bool Freehand => Tool is ScreenshotEditor.Tool.Pen or ScreenshotEditor.Tool.Marker;

    /// <summary>A freehand stroke is being drawn: holding still may snap it.</summary>
    public bool CanSnap => Mode == PointerMode.Draw && Freehand;

    /// <summary>Text and label tools place a text box.</summary>
    public bool TextTool => Tool is ScreenshotEditor.Tool.Text or ScreenshotEditor.Tool.Label;

    /// <summary>Where the current gesture started (WPF units).</summary>
    public Point PressPoint { get; set; }

    /// <summary>The crop when a crop drag started (for its single undo step).</summary>
    public Rect CropAtPress { get; set; }

    /// <summary>The crop handle being dragged (0-7, clockwise from the top left).</summary>
    public int Handle { get; set; }

    /// <summary>The moved item's offset when its drag started.</summary>
    public Vector ItemStart { get; set; }

    /// <summary>Esc was pressed once with changes: the next Esc discards them.</summary>
    public bool DiscardArmed { get; private set; }

    /// <summary>
    /// Esc with changes asks once so nothing gets lost by accident: true means "ask now" (and arms the
    /// next Esc), false means "close".
    /// </summary>
    public bool ConfirmDiscard(bool hasChanges)
    {
        if (!hasChanges || DiscardArmed) return false;
        DiscardArmed = true;
        return true;
    }

    /// <summary>Any other action, or the notice fading out, takes the question back.</summary>
    public void DisarmDiscard() => DiscardArmed = false;

    public void Begin(PointerMode mode) => Mode = mode;

    /// <summary>Ends the gesture and returns what it was.</summary>
    public PointerMode Finish()
    {
        var finished = Mode;
        Mode = PointerMode.None;
        return finished;
    }

    public void Reset() => Mode = PointerMode.None;

    /// <summary>What a press does with the current tool.</summary>
    /// <param name="menuOpen">A tool menu is open.</param>
    /// <param name="typing">A text box is being edited.</param>
    /// <param name="onItem">The press hits a markup item (only asked for the move tool).</param>
    /// <param name="inCrop">The press is inside the crop.</param>
    public PressAction Press(bool menuOpen, bool typing, bool onItem, bool inCrop)
    {
        if (menuOpen) return PressAction.CloseMenu;
        if (typing) return PressAction.CommitText;
        if (Tool == ScreenshotEditor.Tool.Move) return onItem ? PressAction.MoveItem : inCrop ? PressAction.MoveCrop : PressAction.Ignore;
        if (!inCrop) return PressAction.Ignore;
        if (TextTool) return PressAction.StartText;
        return Tool == ScreenshotEditor.Tool.Number ? PressAction.PlaceNumber : PressAction.StartStroke;
    }
}
