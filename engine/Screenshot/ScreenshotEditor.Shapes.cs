namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: showing a held pen stroke as a clean shape (the lifecycle is in PenSnap, the geometry in ShapeRecognizer).
internal sealed partial class ScreenshotEditor
{
    // Pen/highlighter held still: the stroke becomes a clean shape that can still be resized afterwards.
    internal void PenHeld()
    {
        if (!interaction.CanSnap || snap.Snapped || stroke == null) return;
        bool marker = interaction.Tool == Tool.Marker;
        if (!snap.TrySnap(StrokePoints(stroke), marker, style.StrokeWidth, holdPoint)) return;
        if (snap.Arrow) stroke.Fill = stroke.Stroke;
        stroke.Data = snap.Initial(marker, style.StrokeWidth, holdPoint);
        var flash = new DoubleAnimation(0.35, 1, Motion.ShapeFlash);
        stroke.BeginAnimation(OpacityProperty, flash);
    }

    // After snapping: open shapes follow with their end, closed shapes grow around their center.
    void Reshape(Point pointer) => stroke!.Data = snap.Follow(pointer, interaction.Tool == Tool.Marker, style.StrokeWidth);
}
