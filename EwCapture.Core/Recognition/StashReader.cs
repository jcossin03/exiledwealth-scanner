namespace EwCapture.Core.Recognition;

/// <summary>One stack read off a slot. Item is null for an open slot (any item can sit there).</summary>
public sealed record SlotRead(int Slot, string? Item, int Count, double Confidence);

public sealed record TabRead(TabMatch Tab, IReadOnlyList<SlotRead> Reads);

/// <summary>Identifies the special tab on screen and reads every slot's count.</summary>
public sealed class StashReader(LayoutSet layouts, IIconSource icons, CountReader counts)
{
    readonly TabMatcher _matcher = new(layouts, icons);

    /// <summary>The tab's reads, or null when the frame doesn't show a fixed tab we know.</summary>
    public TabRead? Read(Frame frame)
    {
        var f = frame.NormalisedTo1440();
        var tab = _matcher.Match(f);
        if (tab is null) return null;
        var slots = layouts.Layouts[tab.Layout].Slots;
        var each = new SlotRead?[slots.Length];
        Parallel.For(0, slots.Length, i =>
        {
            var s = slots[i];
            if (s.Item is null) return; // open slots: naming their item is v2
            var c = counts.Read(f, (int)Math.Round(s.X + tab.OffsetX), (int)Math.Round(s.Y + tab.OffsetY));
            if (c is { Count: > 0 } r) each[i] = new SlotRead(i, s.Item, r.Count, r.Confidence);
        });
        return new TabRead(tab, each.OfType<SlotRead>().ToList());
    }
}
