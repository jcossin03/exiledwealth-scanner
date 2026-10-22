namespace EwCapture.Core.Recognition;

/// <summary>
/// Finds the open tab in the stash's tab strip. The strip's underline (row 166 at
/// 1440p) is drawn in the open tab's colour, and only the open tab's bottom edge
/// (row 164) carries that same colour: closed tabs fade darker towards the bottom,
/// even ones of the same colour. The strip scrolls, so this gives where the open
/// tab is on screen (to read its name), not its position in the stash.
/// </summary>
public static class TabStrip
{
    const int UnderlineRow = 166, EdgeRow = 164, Left = 0, Right = 820;
    const int MaxColourDistance = 30, MaxGap = 6, MinWidth = 40;
    /// <summary>Rows of the label text, for reading the name.</summary>
    public const int LabelTop = 132, LabelBottom = 162;

    /// <summary>The open tab's horizontal extent on a 1440p frame, or null if no strip is visible.</summary>
    public static (int X0, int X1)? FindOpenTab(Frame f)
    {
        if (f.Height != 1440) f = f.NormalisedTo1440();
        if (f.Width < Right) return null;
        var under = MedianColour(f, UnderlineRow, 100, 800);
        (int X0, int X1)? best = null;
        int start = -1, lastMatch = -1;
        for (int x = Left; x <= Right; x++)
        {
            bool match = x < Right && Distance(f, x, EdgeRow, under) < MaxColourDistance;
            if (match)
            {
                // Small breaks in the edge (ornaments, a gradient step) don't split a tab.
                if (start < 0 || x - lastMatch > MaxGap) { Close(); start = x; }
                lastMatch = x;
            }
        }
        Close();
        return best;

        void Close()
        {
            if (start >= 0 && lastMatch - start + 1 >= MinWidth && (best is null || lastMatch - start > best.Value.X1 - best.Value.X0))
                best = (start, lastMatch);
            start = -1;
        }
    }

    /// <summary>Rows the stash-open check needs, at 1440p: grab just these first.</summary>
    public const int GateTop = 150, GateRows = 24, GateWidth = 820;

    /// <summary>
    /// Is the stash open? Its tab strip has an underline: one solid colour for
    /// some 700 px, with a near-black row under it. The game world never draws
    /// that, so this costs a thin screen copy instead of a full read.
    /// </summary>
    /// <param name="strip">Pixels at 1440p scale, whose row 0 is 1440p row <paramref name="top"/>.</param>
    public static bool StashOpen(Frame strip, int top = GateTop)
    {
        int line = UnderlineRow - top, below = line + 2;
        if (line < 0 || below >= strip.Height || strip.Width < 800) return false;
        var c = MedianColour(strip, line, 100, 800);
        if (c.R + c.G + c.B < 40) return false; // a dark band isn't a coloured underline
        int same = 0, darker = 0;
        for (int x = 100; x < 800; x++)
        {
            if (Distance(strip, x, line, c) < MaxColourDistance) same++;
            int p = (below * strip.Width + x) * 4, q = (line * strip.Width + x) * 4;
            if (strip.Bgra[p] + strip.Bgra[p + 1] + strip.Bgra[p + 2] + 30 < strip.Bgra[q] + strip.Bgra[q + 1] + strip.Bgra[q + 2]) darker++;
        }
        // Half the line is enough: a hover tooltip (a sub-tab name) can cover the rest.
        return same >= 350 && darker >= 630;
    }

    /// <summary>
    /// Is the whole underline visible? A hover tooltip (a sub-tab's name) over the
    /// strip breaks it, and then the label under it can be misread ("Sockets" read
    /// as "ts"), so a name is only trusted when this holds.
    /// </summary>
    public static bool StripUncovered(Frame f)
    {
        if (f.Height != 1440) f = f.NormalisedTo1440();
        if (f.Width < 800) return false;
        var c = MedianColour(f, UnderlineRow, 100, 800);
        int same = 0;
        for (int x = 100; x < 800; x++) if (Distance(f, x, UnderlineRow, c) < MaxColourDistance) same++;
        return same >= 690;
    }

    static (int R, int G, int B) MedianColour(Frame f, int y, int x0, int x1)
    {
        var r = new List<int>(); var g = new List<int>(); var b = new List<int>();
        for (int x = x0; x < x1; x++)
        {
            int p = (y * f.Width + x) * 4;
            b.Add(f.Bgra[p]); g.Add(f.Bgra[p + 1]); r.Add(f.Bgra[p + 2]);
        }
        static int Mid(List<int> v) { v.Sort(); return v[v.Count / 2]; }
        return (Mid(r), Mid(g), Mid(b));
    }

    static int Distance(Frame f, int x, int y, (int R, int G, int B) c)
    {
        int p = (y * f.Width + x) * 4;
        return Math.Abs(f.Bgra[p + 2] - c.R) + Math.Abs(f.Bgra[p + 1] - c.G) + Math.Abs(f.Bgra[p] - c.B);
    }

    /// <summary>A tab name as read (OCR) cleaned up: trimmed, spaces collapsed, the special-tab mark dropped.</summary>
    public static string CleanName(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        // Special tabs end in a small "remove-only" emblem that OCR turns into a stray symbol or letter.
        while (words.Count > 1 && !words[^1].Any(char.IsLetterOrDigit)) words.RemoveAt(words.Count - 1);
        if (words.Count > 1 && words[^1].Length == 1 && !char.IsDigit(words[^1][0])) words.RemoveAt(words.Count - 1);
        var name = string.Join(' ', words).Trim();
        return name.Length > 64 ? name[..64] : name;
    }
}
