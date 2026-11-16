namespace EwCapture;

/// <summary>
/// A message-only window Windows tells when the clipboard changes (AddClipboardFormatListener). No
/// polling and no keyboard hook: it learns that the clipboard changed, never which keys were pressed.
/// It exists only while the player has item copies turned on; whether a change is read is App's call.
/// </summary>
sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    readonly Action _onChanged;
    public bool Listening { get; }

    public ClipboardWatcher(Action onChanged)
    {
        _onChanged = onChanged;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        Listening = Native.AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) _onChanged();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Listening) Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
