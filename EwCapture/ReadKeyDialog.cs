using EwCapture.Core;

namespace EwCapture;

/// <summary>
/// Pick the read key by pressing it. Opened from the tray menu; the caller lets go of the
/// current key first, or pressing it here would read a tab instead of being recorded.
/// </summary>
sealed class ReadKeyDialog : Form
{
    readonly Func<ReadKey, bool> _tryUse;
    readonly Label _keyLabel = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 16, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
    readonly Label _note = new() { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = SystemColors.GrayText };
    readonly NoFocusButton _save = new() { Text = "Save", AutoSize = true, Enabled = false };
    ReadKey? _candidate;

    /// <summary>The key saved, once the dialog closed with Save.</summary>
    public ReadKey? Chosen => DialogResult == DialogResult.OK ? _candidate : null;

    /// <param name="tryUse">Registers the key; false when another program already holds it.</param>
    public ReadKeyDialog(ReadKey current, Func<ReadKey, bool> tryUse)
    {
        _tryUse = tryUse;
        Text = "Read key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        TopMost = true;

        var intro = new Label { Text = "Press the new key combination.", AutoSize = true };
        _keyLabel.Text = current.ToString();

        var reset = new NoFocusButton { Text = $"Use {ReadKey.Default}", AutoSize = true };
        reset.Click += (_, _) => Choose(ReadKey.Default);
        var cancel = new NoFocusButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _save.Click += (_, _) => Save();

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([_save, cancel, reset]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.AddRange([intro, _keyLabel, _note, buttons]);
        Controls.Add(layout);
        CancelButton = cancel;
        Shown += (_, _) => Activate(); // opened from the tray, it would otherwise come up behind
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = (int)(keyData & Keys.KeyCode);
        bool ctrl = keyData.HasFlag(Keys.Control), alt = keyData.HasFlag(Keys.Alt), shift = keyData.HasFlag(Keys.Shift);
        if (key == (int)Keys.Escape && !ctrl && !alt && !shift) { DialogResult = DialogResult.Cancel; return true; }
        if (ReadKey.IsModifier(key))
        {
            // Modifiers held so far, waiting for the key that goes with them.
            _keyLabel.Text = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + "…";
            return true;
        }
        Choose(new ReadKey(ctrl, alt, shift, key));
        return true;
    }

    void Choose(ReadKey key)
    {
        _keyLabel.Text = key.ToString();
        var blocker = key.Blocker();
        _candidate = blocker is null ? key : null;
        _save.Enabled = blocker is null;
        _note.ForeColor = blocker is null && key.Warning() is not null ? Color.DarkOrange : SystemColors.GrayText;
        _note.Text = blocker ?? key.Warning() ?? "";
    }

    void Save()
    {
        if (_candidate is not { } key) return;
        if (!_tryUse(key))
        {
            _note.Text = $"Another program uses {key}.";
            _save.Enabled = false;
            return;
        }
        DialogResult = DialogResult.OK;
    }

    /// <summary>A button that never takes focus, so Enter, Space and Tab reach ProcessCmdKey as keys to record.</summary>
    sealed class NoFocusButton : Button
    {
        public NoFocusButton() => SetStyle(ControlStyles.Selectable, false);
    }
}
