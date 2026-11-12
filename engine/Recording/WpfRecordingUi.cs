namespace MosaikCapture.Engine.Recording;

/// <summary>The real recording windows. Each window is created on first use.</summary>
internal sealed class WpfRecordingUi : IRecordingUi
{
    SelectionOverlay? overlay;
    RecordingFrame? frame;
    RecordingToolbar? toolbar;
    CountdownWindow? countdown;
    ClickHighlighter? clicks;
    RecordingCard? card;
    TrimWindow? trimWindow;

    public RecordingCard Card => card ??= new RecordingCard();
    public TrimWindow TrimWindow => trimWindow ??= new TrimWindow();
    RecordingFrame Frame => frame ??= new RecordingFrame();
    RecordingToolbar Toolbar => toolbar ??= new RecordingToolbar();
    CountdownWindow Countdown => countdown ??= new CountdownWindow();
    ClickHighlighter Clicks => clicks ??= new ClickHighlighter();

    public RECT WorkAreaAtCursor() => NativeMethods.WorkAreaAtCursor();

    public void PickArea(Action<Int32Rect, RECT, RECT> picked, Action cancelled)
    {
        overlay ??= new SelectionOverlay(null);
        overlay.Recorded = picked;
        overlay.Aborted = cancelled;
        overlay.OpenForRecording();
    }

    public void ShowFrame(Int32Rect area, RECT monitor) => Frame.Present(area, monitor);
    public void HideFrame() => Frame.Hide();
    public void SetFramePaused(bool paused) => Frame.SetPaused(paused);

    public void StartCountdown(Int32Rect area, RECT monitor, Action launch, Action finished, Action cancelled) =>
        Countdown.Run(area, monitor, launch, finished, cancelled);

    public void StopCountdown() => Countdown.Stop();

    public void ShowToolbar(Int32Rect area, RECT work) => Toolbar.Present(area, work);
    public void RefreshToolbar() => Toolbar.Refresh();
    public void HideToolbar() => Toolbar.Hide();

    public void StartClickHighlights(Int32Rect area, bool enabled)
    {
        Clicks.Enabled = enabled;
        Clicks.Start(area, Toolbar.Handle);
    }

    public void SetClickHighlights(bool enabled) => Clicks.Enabled = enabled;
    public void StopClickHighlights() => Clicks.Stop();

    public void ShowSaving(double seconds, RECT work) => Card.Saving(seconds, work);
    public void ShowProblem(string heading, string reason, RECT work) => Card.Problem(heading, reason, work);
    public void ShowRecording(RecordedVideo video, string heading, RECT work) => Card.Present(video, heading, work);

    // As a file on the clipboard: pasting into chat, mail or Explorer attaches the video.
    public void CopyFileToClipboard(string path)
    {
        var files = new System.Collections.Specialized.StringCollection { path };
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { Clipboard.SetFileDropList(files); return; }
            catch (ExternalException) { Thread.Sleep(40); }   // clipboard briefly held by another app
        }
    }

    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}

/// <summary>The real system side of a recording: ffmpeg, DXGI, settings and work folders.</summary>
internal sealed class RecordingBackend : IRecordingBackend
{
    static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    public bool FfmpegAvailable => Ffmpeg.Available;

    public void PrepareEncoder() => _ = Ffmpeg.DetectEncoderAsync();

    public bool TryFindOutput(RECT monitor, out int output, out RECT desktop) => Dxgi.Find(monitor, out output, out desktop);

    public IRecordingSession StartSession(Int32Rect area, int output, RECT desktop, AudioSettings audio)
    {
        var session = new RecordingSession(area, output, desktop, audio);
        session.StartAudio();
        return session;
    }

    public string NewRecordingPath(DateTime begun)
    {
        string folder = CaptureSettings.RecordingFolder;
        Directory.CreateDirectory(folder);
        return UniqueFile.Next(Path.Combine(folder, FileName(begun)));
    }

    /// <summary>"Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4"</summary>
    internal static string FileName(DateTime begun) =>
        UiText.Files.RecordingPrefix + begun.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.CurrentCulture) + ".mp4";

    public AudioSettings LoadSettings() => new(CaptureSettings.Microphone, CaptureSettings.SystemAudio, CaptureSettings.Clicks);

    public void SaveSettings(AudioSettings settings) => CaptureSettings.SaveAudio(settings.Microphone, settings.SystemSound, settings.ShowClicks);

    // Leftovers of aborted recordings (e.g. after a crash), also in the folder older versions used.
    public void RemoveLeftovers()
    {
        RemoveOldWorkFolders(CaptureSettings.RecordingWorkRoot);
        RemoveOldWorkFolders(CaptureSettings.LegacyRecordingWorkRoot);
        try { if (Directory.Exists(CaptureSettings.LegacyRecordingWorkRoot)) Directory.Delete(CaptureSettings.LegacyRecordingWorkRoot); }
        catch (IOException) { }   // not empty yet (an older version may still be recording): next time
        catch (UnauthorizedAccessException) { }
    }

    internal static void RemoveOldWorkFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string folder in Directory.GetDirectories(root))
            {
                if (DateTime.Now - Directory.GetCreationTime(folder) <= LeftoverAge) continue;
                try { Directory.Delete(folder, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // still in use: next time
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Removing old recording work folders", ex);
        }
    }
}
