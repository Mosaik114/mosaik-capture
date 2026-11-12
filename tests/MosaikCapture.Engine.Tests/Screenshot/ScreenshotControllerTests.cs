namespace MosaikCapture.Engine.Tests.Screenshot;

// The screenshot flow overlay → save and copy → card → editor with fake windows, files and clipboard.
public sealed class ScreenshotControllerTests
{
    readonly FakeUi ui = new();
    readonly FakeStore store = new();
    readonly ScreenshotController controller;

    public ScreenshotControllerTests() => controller = new ScreenshotController(ui, store);

    static ScreenCapture Capture() => WpfThread.Run(() =>
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        image.Freeze();
        return new ScreenCapture { FullScreen = image, Image = image, Crop = new Int32Rect(0, 0, 2, 2), Scale = 1, VirtualWidth = 2, VirtualHeight = 2 };
    });

    [Fact]
    public void ControllerBindsItselfToTheWindows() => Assert.Same(controller, ui.Flow);

    [Fact]
    public void StartOpensTheOverlayUnlessTheEditorIsOpen()
    {
        controller.Start();
        Assert.Equal(new[] { "OpenOverlay" }, ui.Calls);

        ui.EditorOpen = true;
        controller.Start();
        Assert.Single(ui.Calls);
    }

    [Fact]
    public async Task CaptureIsSavedCopiedAndShown()
    {
        var capture = Capture();

        await controller.CapturedAsync(capture);

        Assert.Equal("C:\\Shots\\new.png", capture.Path);
        Assert.Equal(new[] { "C:\\Shots\\new.png" }, store.Saved);
        Assert.Equal(new[] { "ShowCard In Zwischenablage kopiert", "CardSaved" }, ui.Calls);
        Assert.Same(capture, controller.Current);
    }

    [Fact]
    public async Task BusyClipboardIsNotReportedAsCopied()
    {
        store.ClipboardWorks = false;

        await controller.CapturedAsync(Capture());

        Assert.Equal("CardSavedNotCopied", ui.Calls.Last());
        Assert.DoesNotContain("CardSaved", ui.Calls);
    }

    [Fact]
    public async Task UnwritableFolderShowsNotSaved()
    {
        store.SaveFailure = new UnauthorizedAccessException("denied");

        await controller.CapturedAsync(Capture());

        Assert.Equal("CardFailed", ui.Calls.Last());
        Assert.Empty(store.Copied);
    }

    [Fact]
    public async Task CaptureWhileEditingIsIgnored()
    {
        ui.EditorOpen = true;

        await controller.CapturedAsync(Capture());

        Assert.Empty(ui.Calls);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task EditOpensTheEditorAndHidesTheCard()
    {
        var capture = Capture();
        await controller.CapturedAsync(capture);
        ui.Calls.Clear();

        controller.Edit();

        Assert.Equal(new[] { "OpenEditor", "HideCard" }, ui.Calls);
    }

    [Fact]
    public void EditWithoutACaptureDoesNothing()
    {
        controller.Edit();
        controller.Pin();

        Assert.Empty(ui.Calls);
    }

    [Fact]
    public async Task EditedImageReplacesTheFile()
    {
        var capture = Capture();
        await controller.CapturedAsync(capture);
        var edited = Capture().Image;
        ui.Calls.Clear();

        await controller.EditedAsync(capture, edited);

        Assert.Same(edited, capture.Image);
        Assert.Equal(new[] { "C:\\Shots\\new.png", "C:\\Shots\\new.png" }, store.Saved);
        Assert.Equal(new[] { "ShowCard Bearbeitet und kopiert", "CardSaved" }, ui.Calls);
    }

    [Fact]
    public async Task EditedAndPinnedPinsTheEditedImage()
    {
        var capture = Capture();
        await controller.CapturedAsync(capture);
        var edited = Capture().Image;
        ui.Calls.Clear();

        await controller.EditedAndPinnedAsync(capture, edited);

        Assert.Equal("ShowCard Bearbeitet und kopiert", ui.Calls[0]);   // pinning does not wait for the file
        Assert.Contains("PinInPlace", ui.Calls);
        Assert.Contains("CardSaved", ui.Calls);
        Assert.Same(edited, ui.PinnedImage);
    }

    [Fact]
    public async Task CancelledEditShowsTheCardAgainWithoutClaimingACopyThatFailed()
    {
        store.ClipboardWorks = false;
        var capture = Capture();
        await controller.CapturedAsync(capture);
        ui.Calls.Clear();

        controller.EditCancelled(capture);

        Assert.Equal(new[] { "ShowCard Gespeichert, nicht kopiert", "CardSaved" }, ui.Calls);
    }

    [Fact]
    public async Task ClosedCardReleasesTheCaptureUnlessTheEditorUsesIt()
    {
        await controller.CapturedAsync(Capture());
        ui.EditorOpen = true;
        controller.CardClosed();
        Assert.NotNull(controller.Current);

        ui.EditorOpen = false;
        controller.CardClosed();
        Assert.Null(controller.Current);
    }

    [Fact]
    public void WindowErrorClosesEverythingAndTellsTheUser()
    {
        ui.FailOn = "OpenOverlay";

        controller.Start();

        Assert.Equal(new[] { "CloseAll", "ShowProblem Screenshot fehlgeschlagen | window broke" }, ui.Calls.Skip(1));
    }

    [Fact]
    public void UnreadableScreenTellsTheUserInGerman()
    {
        ui.FailWith = new System.ComponentModel.Win32Exception(5, "Copying the screen failed");

        controller.Start();

        Assert.Equal("ShowProblem Screenshot fehlgeschlagen | " + UiText.ScreenshotCard.ScreenUnavailable, ui.Calls.Last());
    }

    [Fact]
    public void InvalidScreenAreaThrowsWin32Exception() =>
        Assert.Throws<System.ComponentModel.Win32Exception>(() => ScreenGrabber.Capture(0, 0, -5, 10, 96));

    [Fact]
    public async Task ErrorWhileDeliveringRecoversAndTheNextScreenshotWorks()
    {
        ui.FailOn = "ShowCard";
        await controller.CapturedAsync(Capture());
        Assert.Null(controller.Current);
        Assert.Contains("CloseAll", ui.Calls);

        ui.FailOn = null;
        ui.Calls.Clear();
        await controller.CapturedAsync(Capture());
        Assert.Equal("CardSaved", ui.Calls.Last());
    }

    sealed class FakeUi : IScreenshotUi
    {
        public readonly List<string> Calls = new();
        public IScreenshotFlow? Flow;
        public string? FailOn;
        public Exception? FailWith;
        public BitmapSource? PinnedImage;

        public bool EditorOpen { get; set; }

        void Log(string call)
        {
            lock (Calls) Calls.Add(call);
            if (FailOn != null && call.StartsWith(FailOn, StringComparison.Ordinal)) throw new InvalidOperationException("window broke");
            if (FailWith != null && call == "OpenOverlay") throw FailWith;
        }

        public void Bind(IScreenshotFlow flow) => Flow = flow;
        public void OpenOverlay() => Log("OpenOverlay");
        public void OpenEditor(ScreenCapture capture) => Log("OpenEditor");
        public void ShowCard(ScreenCapture capture, string heading) => Log("ShowCard " + heading);
        public void CardSaved() => Log("CardSaved");
        public void CardSavedNotCopied() => Log("CardSavedNotCopied");
        public void CardFailed() => Log("CardFailed");
        public void HideCard() => Log("HideCard");

        public void PinInPlace(ScreenCapture capture)
        {
            PinnedImage = capture.Image;
            Log("PinInPlace");
        }

        public void CloseAll() => Log("CloseAll");
        public void ShowProblem(string heading, string detail) => Log("ShowProblem " + heading + " | " + detail);
    }

    sealed class FakeStore : IScreenshotStore
    {
        public readonly List<string> Saved = new();
        public readonly List<byte[]> Copied = new();
        public bool ClipboardWorks = true;
        public Exception? SaveFailure;

        public string NewPath(DateTime when) => "C:\\Shots\\new.png";

        public Task<byte[]> SaveAsync(BitmapSource image, string path)
        {
            if (SaveFailure != null) return Task.FromException<byte[]>(SaveFailure);
            Saved.Add(path);
            return Task.FromResult(new byte[] { 0x89 });
        }

        public Task<bool> CopyAsync(BitmapSource image, byte[] png)
        {
            Copied.Add(png);
            return Task.FromResult(ClipboardWorks);
        }
    }
}
