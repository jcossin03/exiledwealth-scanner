namespace EwCapture;

/// <summary>
/// A message-only window that owns one global hotkey. RegisterHotKey, not a
/// low-level keyboard hook: Windows hands us this one combination and nothing else.
/// </summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int Id = 1;
    readonly Action _onPressed;
    public bool Registered { get; }

    public HotkeyWindow(uint modifiers, Keys key, Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        Registered = Native.RegisterHotKey(Handle, Id, modifiers | Native.MOD_NOREPEAT, (uint)key);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam == Id) _onPressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}
