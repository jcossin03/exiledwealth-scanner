namespace EwCapture;

/// <summary>A one-field dialog. Only ever opened from the tray menu, never mid-game.</summary>
static class Prompt
{
    public static string? Ask(string title, string message, string initial = "", bool upperCase = false)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            TopMost = true,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label { Text = message, AutoSize = true, MaximumSize = new Size(360, 0) };
        var box = new TextBox
        {
            Text = initial,
            Width = 360,
            CharacterCasing = upperCase ? CharacterCasing.Upper : CharacterCasing.Normal,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        layout.Controls.AddRange([label, box, ok]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
