namespace MosaikCapture.Engine.Core;

/// <summary>Shared animation and UI timing, so all windows move alike and the numbers have names.</summary>
internal static class Motion
{
    /// <summary>Lets whatever triggered a command (tray menu, hotkey overlay) leave the screen first.</summary>
    public static readonly TimeSpan TriggerDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>A freshly shown full-screen window stays cloaked this long so its first frame is complete.</summary>
    public static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>Cards slide in from the screen edge.</summary>
    public static readonly Duration SlideIn = new(TimeSpan.FromMilliseconds(200));

    /// <summary>Overlays and frames fade in.</summary>
    public static readonly Duration FadeIn = new(TimeSpan.FromMilliseconds(140));

    /// <summary>Cards fade out at the end of their lifetime.</summary>
    public static readonly Duration FadeOut = new(TimeSpan.FromMilliseconds(260));

    /// <summary>Hover feedback on small buttons.</summary>
    public static readonly Duration HoverFade = new(TimeSpan.FromMilliseconds(120));

    /// <summary>A card stays this long after the pointer left it.</summary>
    public static readonly TimeSpan LingerAfterLeave = TimeSpan.FromMilliseconds(1200);

    /// <summary>Short confirmations (copied, zoom level) on pinned screenshots.</summary>
    public static readonly TimeSpan FeedbackChip = TimeSpan.FromMilliseconds(1100);

    /// <summary>An error stays visible before the normal controls return.</summary>
    public static readonly TimeSpan ErrorShown = TimeSpan.FromSeconds(4);

    /// <summary>One sweep of the indeterminate progress bar.</summary>
    public static readonly Duration IndeterminateSweep = new(TimeSpan.FromMilliseconds(1100));

    /// <summary>The red recording dot pulses with this period.</summary>
    public static readonly Duration RecordingPulse = new(TimeSpan.FromMilliseconds(800));

    /// <summary>Countdown animation frame and the pulse of each new number.</summary>
    public static readonly TimeSpan CountdownFrame = TimeSpan.FromMilliseconds(15);
    public static readonly Duration CountdownPulse = new(TimeSpan.FromMilliseconds(260));

    /// <summary>A recognized shape flashes once.</summary>
    public static readonly Duration ShapeFlash = new(TimeSpan.FromMilliseconds(200));
}
