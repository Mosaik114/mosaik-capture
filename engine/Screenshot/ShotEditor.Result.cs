namespace MosaikCapture.Engine.Screenshot;

// Screenshot editor: undo, keyboard and result (fields and construction in ShotEditor.cs).
partial class ShotEditor
{
    // ---------- Undo ----------

    void Add(FrameworkElement el)
    {
        // pixelated and blurred areas belong to the image: at the very bottom, below dimming and markup
        Action put = delegate { if ("cover".Equals(el.Tag)) ink.Children.Insert(0, el); else ink.Children.Add(el); };
        put();
        Push(delegate { ink.Children.Remove(el); }, put);
        RefreshShade(null);
    }

    void Push(Action u, Action r)
    {
        undo.Add(new Step(u, r));
        redo.Clear();
    }

    void Undo()
    {
        Commit();
        if (undo.Count == 0) return;
        var st = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        st.Undo();
        redo.Add(st);
        RefreshShade(null);
        UpdateGhost();
    }

    void Redo()
    {
        Commit();
        if (redo.Count == 0) return;
        var st = redo[redo.Count - 1];
        redo.RemoveAt(redo.Count - 1);
        st.Redo();
        undo.Add(st);
        RefreshShade(null);
        UpdateGhost();
    }

    void RemovePicked()
    {
        var el = picked;
        if (el == null) return;
        int idx = ink.Children.IndexOf(el);
        if (idx < 0) return;
        picked = null;
        ink.Children.RemoveAt(idx);
        Push(delegate { ink.Children.Insert(Math.Min(idx, ink.Children.Count), el); }, delegate { ink.Children.Remove(el); });
        RefreshShade(null);
    }

    // ---------- Keyboard ----------

    void OnKey(KeyEventArgs e)
    {
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (typing != null)
        {
            if (k == Key.Escape || (k == Key.Enter && !shift)) { Commit(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        if (k == Key.Escape && menuFor != null) { CloseMenu(); return; }
        if (ctrl && k == Key.Z && !shift) Undo();
        else if (ctrl && (k == Key.Y || (k == Key.Z && shift))) Redo();
        else if (ctrl && shift && k == Key.C) CopyText();
        else if (ctrl && k == Key.C) CopyNow();
        else if (ctrl && k == Key.S) SaveAs();
        else if (k == Key.Enter) Finish(false);
        else if (k == Key.F3) Finish(true);
        else if (k == Key.Escape) Cancel();
        else if (k == Key.Delete || k == Key.Back) RemovePicked();
        else if (!ctrl && Array.IndexOf(ToolKeys, k) >= 0) SetTool((Tool)Array.IndexOf(ToolKeys, k));
        else if (k == Key.OemPlus || k == Key.Add || k == Key.OemCloseBrackets) StepWidth(1);
        else if (k == Key.OemMinus || k == Key.Subtract || k == Key.OemOpenBrackets) StepWidth(-1);
        else if (k >= Key.D1 && k <= Key.D6) SetColor(k - Key.D1);
        else if (k >= Key.NumPad1 && k <= Key.NumPad6) SetColor(k - Key.NumPad1);
        else e.Handled = false;
    }

    // ---------- Result ----------

    // The crop including all markup at full resolution.
    BitmapSource Render()
    {
        Int32Rect c = Cut();
        double w = c.Width / s, h = c.Height / s;
        var rtb = new RenderTargetBitmap(c.Width, c.Height, 96 * s, 96 * s, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawImage(new CroppedBitmap(data.Full, c), new Rect(0, 0, w, h));
        rtb.Render(dv);
        if (ink.Children.Count > 0)
        {
            // shift the markup so the top-left corner of the crop is at 0,0
            Transform old = ink.RenderTransform;
            ink.RenderTransform = new TranslateTransform(-c.X / s, -c.Y / s);
            ink.UpdateLayout();
            rtb.Render(ink);
            ink.RenderTransform = old;
            ink.UpdateLayout();
        }
        rtb.Freeze();
        return rtb;
    }

    // Recognizes the text in the crop and copies it as text.
    async void CopyText()
    {
        Commit();
        var crop = new CroppedBitmap(data.Full, Cut());
        crop.Freeze();
        Note("Text wird gelesen …");
        string? text = await ShotText.Read(crop);
        if (!IsVisible) return;
        if (string.IsNullOrEmpty(text)) { Note("Kein Text gefunden"); return; }
        ShotText.ToClipboard(text!);
        Note("Text kopiert · " + ShotText.Summary(text!));
    }

    void CopyNow()
    {
        Commit();
        var img = Render();
        Shot.ToClipboard(img, Shot.Encode(img, false));
        Note("In Zwischenablage kopiert");
    }

    void SaveAs()
    {
        Commit();
        string? saved = Shot.SaveAs(this, Render(), data.Path);
        if (saved != null) Note(saved.Length > 0 ? "Gespeichert: " + saved : "Konnte nicht speichern");
    }

    // Done: save and copy as usual; with pin, also pin the image in place.
    void Finish(bool pin)
    {
        Commit();
        var img = Render();
        var d = data;
        d.Cut = Cut();
        Leave();
        if (pin) pinned(d, img); else done(d, img);
    }

    // Esc with changes asks once so nothing gets lost by accident.
    void Cancel()
    {
        Commit();
        if (undo.Count > 0 && !escArmed)
        {
            escArmed = true;
            Note("Nochmal Esc = Änderungen verwerfen");
            return;
        }
        var d = data;
        Leave();
        cancelled(d);
    }

    void Leave()
    {
        ShotOverlay.Cloak(hwnd, true);
        if (IsMouseCaptured) ReleaseMouseCapture();
        mode = Mode.None;
        holdTimer.Stop();
        CloseMenu();
        Hide();
        ink.Children.Clear();
        undo.Clear();
        redo.Clear();
        back.Source = null;
        note.Visibility = tip.Visibility = Visibility.Hidden;
        data = null!;
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
