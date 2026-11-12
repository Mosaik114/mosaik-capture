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
