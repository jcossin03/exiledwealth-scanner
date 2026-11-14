namespace EwCapture.Core;

/// <summary>
/// The key combination the player presses to read the open stash tab. GGG allows a screen read
/// only once per manual key press, so this key is the scanner's only trigger, and the player
/// picks it. <see cref="Vk"/> is a Windows virtual-key code, so this needs no WinForms.
/// </summary>
public readonly record struct ReadKey(bool Ctrl, bool Alt, bool Shift, int Vk)
{
    const int F1 = 0x70, F4 = 0x73, F24 = 0x87;

    /// <summary>Ctrl+Shift+F12, the key every version before 0.5.0 used.</summary>
    public static readonly ReadKey Default = new(Ctrl: true, Alt: false, Shift: true, Vk: 0x7B);

    /// <summary>Shift, Ctrl, Alt or Windows on their own (either side).</summary>
    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

    /// <summary>Why this can't be a key at all, or null. Only a modifier on its own is refused.</summary>
    public string? Blocker() => IsModifier(Vk) ? "Hold the modifiers and press one more key." : null;

    /// <summary>
    /// A clash worth knowing about, or null. Windows hands a registered key to the scanner instead
    /// of the game, so a key the game or Windows uses by default stops working there. Players rebind
    /// keys, so this only warns: the player decides.
    /// </summary>
    public string? Warning()
    {
        bool fKey = Vk is >= F1 and <= F24;
        if (!Ctrl && !Alt && !fKey)
            return "On its own, this key won't reach the game, chat or any other program while Exiled Wealth runs.";
        if (Ctrl && !Alt && !Shift && Vk is 'C' or 'V' or 'X' or 'A' or 'F')
            return "By default the game uses this one (copy an item, paste, select all, search the stash). Fine if you've rebound it.";
        if (Ctrl && Alt && !Shift && Vk == 'C')
            return "By default the game uses this for an advanced item copy. Fine if you've rebound it.";
        if (Alt && !Ctrl && !Shift && Vk == F4) return "Alt+F4 closes windows: they won't close with it while Exiled Wealth runs.";
        return null;
    }

    /// <summary>As the tray menu and messages show it: "Ctrl+Shift+F12".</summary>
    public override string ToString() =>
        (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + KeyName(Vk);

    /// <summary>The settings-file form: modifier letters, a colon, the virtual-key code ("CS:123").</summary>
    public string Store() => $"{(Ctrl ? "C" : "")}{(Alt ? "A" : "")}{(Shift ? "S" : "")}:{Vk}";

    /// <summary>A stored key, or null if the text isn't one (a hand-edited or damaged file).</summary>
    public static ReadKey? Parse(string? text)
    {
        var parts = text?.Trim().Split(':');
        if (parts is not [var mods, var code] || !int.TryParse(code, out var vk) || vk is <= 0 or > 0xFE) return null;
        if (mods.Any(c => c is not ('C' or 'A' or 'S'))) return null;
        return new ReadKey(mods.Contains('C'), mods.Contains('A'), mods.Contains('S'), vk);
    }

    static string KeyName(int vk) => vk switch
    {
        >= F1 and <= F24 => $"F{vk - F1 + 1}",
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete", 0x91 => "Scroll Lock",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"key {vk}",
    };
}
