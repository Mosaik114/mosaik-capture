using System.Collections.Specialized;

namespace MosaikCapture.Engine.Core;

/// <summary>
/// The one way the engine writes to the clipboard. Every write runs on its own short-lived STA thread,
/// so a clipboard held by another app never blocks the UI: WPF's own retry loop (OleSetClipboard,
/// up to about a second) is the only retry layer, and the caller gets the result after at most
/// <see cref="Timeout"/>. Data is flushed, so it stays on the clipboard after the thread ends.
/// </summary>
internal static class ClipboardWriter
{
    /// <summary>Longest time a caller waits for the result.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>The PNG format browsers and chat apps prefer over the bitmap.</summary>
    const string PngFormat = "PNG";

    public static Task<bool> SetTextAsync(string text) => SetAsync("text", () => Clipboard.SetText(text));

    /// <summary>As a bitmap and additionally as PNG so browsers and chat apps paste it cleanly.</summary>
    public static Task<bool> SetImageAsync(BitmapSource image, byte[] png)
    {
        var shared = image.IsFrozen ? image : (BitmapSource)image.GetAsFrozen();
        return SetAsync("image", () =>
        {
            var data = new DataObject();
            data.SetImage(shared);
            data.SetData(PngFormat, new MemoryStream(png));
            Clipboard.SetDataObject(data, true);
        });
    }

    /// <summary>As a file: pasting into chat, mail or Explorer attaches it.</summary>
    public static Task<bool> SetFileAsync(string path) => SetAsync("file", () => Clipboard.SetFileDropList(new StringCollection { path }));

    /// <summary>Runs <paramref name="write"/> on an STA thread; false (and logged) if it failed or took too long.</summary>
    internal static async Task<bool> SetAsync(string what, Action write)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                write();
                result.TrySetResult(true);
            }
            catch (Exception ex)   // must not escape a background thread; COM, clipboard and argument errors all mean "not copied"
            {
                ErrorLog.Write("Copying the " + what + " to the clipboard", ex);
                result.TrySetResult(false);
            }
        })
        { IsBackground = true, Name = "Mosaik Capture clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (await Task.WhenAny(result.Task, Task.Delay(Timeout)).ConfigureAwait(false) == result.Task) return await result.Task.ConfigureAwait(false);
        ErrorLog.Write("Copying the " + what + " to the clipboard took too long", null);
        return false;
    }
}
