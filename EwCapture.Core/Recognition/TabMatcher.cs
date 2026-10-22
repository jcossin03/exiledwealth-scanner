namespace EwCapture.Core.Recognition;

public sealed record TabMatch(string Layout, double Score, int OffsetX, int OffsetY, double RunnerUpScore);

/// <summary>
/// Works out which special tab is on screen with no UI art at all: each layout is
/// scored by how well its slots match their items' pictures (masked normalised
/// cross-correlation). Empty fixed slots show their item dimmed, so they count
/// too. Below <see cref="MinScore"/> the capture isn't a fixed tab we know.
/// </summary>
public sealed class TabMatcher
{
    public const double MinScore = 0.30;
    public const double MinLead = 0.10;
    const int Size = 56;        // comparison size; slots are ~70px at 1440p
    const int MaxSlotSide = 150; // bigger slots (2x4 and up) don't score well as squares

    readonly LayoutSet _layouts;
    readonly IIconSource _icons;
    readonly Dictionary<string, (float[] Ref, bool[] Mask, int N)?> _refs = new();

    public TabMatcher(LayoutSet layouts, IIconSource icons) => (_layouts, _icons) = (layouts, icons);

    /// <summary>The best layout, or null when nothing fits well enough.</summary>
    public TabMatch? Match(Frame frame)
    {
        var f = frame.NormalisedTo1440();
        var gray = f.Gray();
        // Cheap ranking first (a dozen slots at the calibrated offset), then a proper
        // search for the best two only: the scanner runs this every couple of seconds.
        var ranked = _layouts.Layouts
            .Select(kv => (Key: kv.Key, Fit: Fit(f, gray, kv.Value, [(kv.Value.Offset[0], kv.Value.Offset[1])], RankSlots)))
            .OrderByDescending(r => r.Fit.Score)
            .Take(2)
            .Select(r => (r.Key, Fit: Refine(f, gray, _layouts.Layouts[r.Key])))
            .OrderByDescending(r => r.Fit.Score)
            .ToList();
        if (ranked.Count == 0) return null;
        var (key, best) = ranked[0];
        double runner = ranked.Count > 1 ? ranked[1].Fit.Score : 0;
        if (best.Score < MinScore || best.Score - runner < MinLead) return null;
        return new TabMatch(key, best.Score, best.Dx, best.Dy, runner);
    }

    const int RankSlots = 12;

    /// <summary>Coarse-to-fine offset search around the layout's calibrated offset.</summary>
    (double Score, int Dx, int Dy) Refine(Frame f, float[] gray, Layout layout)
    {
        var coarse = Fit(f, gray, layout, Around(layout.Offset, 2, 2), int.MaxValue);
        return Fit(f, gray, layout, Around([coarse.Dx, coarse.Dy], 1, 1), int.MaxValue);
    }

    static IEnumerable<(int, int)> Around(int[] o, int radius, int step)
    {
        for (int dx = -radius; dx <= radius; dx += step)
            for (int dy = -radius; dy <= radius; dy += step)
                yield return (o[0] + dx, o[1] + dy);
    }

    (double Score, int Dx, int Dy) Fit(Frame f, float[] gray, Layout layout, IEnumerable<(int, int)> offsets, int maxSlots)
    {
        // Pictures are resolved here, on one thread; the scoring below runs in parallel.
        var scored = layout.Slots
            .Where(s => s.Item is not null && s.Icon is not null && s.W < MaxSlotSide)
            .Select(s => (Slot: s, Ref: Reference(s.Icon!)))
            .Where(t => t.Ref is not null)
            .Take(maxSlots)
            .ToArray();
        if (scored.Length == 0) return (-1, 0, 0);
        (double Score, int Dx, int Dy) best = (-1, 0, 0);
        foreach (var (dx, dy) in offsets)
        {
            var each = new double[scored.Length];
            Parallel.For(0, scored.Length, i =>
            {
                var (s, r) = scored[i];
                each[i] = SlotScore(f, gray, s.X + dx, s.Y + dy, s.W, r!.Value);
            });
            double mean = each.Sum() / scored.Length;
            if (mean > best.Score) best = (mean, dx, dy);
        }
        return best;
    }

    /// <summary>Masked NCC of one slot against its item picture.</summary>
    static double SlotScore(Frame f, float[] gray, double x, double y, double w, (float[] Ref, bool[] Mask, int N) r)
    {
        double inset = w * 0.08;
        int x0 = (int)(x + inset), y0 = (int)(y + inset), x1 = (int)(x + w - inset), y1 = (int)(y + w - inset);
        var crop = new float[Size * Size];
        new Plane(f.Width, f.Height, 1, []).ResampleInto(x0, y0, x1 - x0, y1 - y0, Size, Size, crop, gray);
        double mean = 0;
        for (int i = 0; i < crop.Length; i++) if (r.Mask[i]) mean += crop[i];
        mean /= r.N;
        double var = 0;
        for (int i = 0; i < crop.Length; i++) if (r.Mask[i]) { double d = crop[i] - mean; var += d * d; }
        double sd = Math.Sqrt(var / r.N) + 1e-6, dot = 0;
        for (int i = 0, k = 0; i < crop.Length; i++) if (r.Mask[i]) dot += (crop[i] - mean) / sd * r.Ref[k++];
        return dot / r.N;
    }

    /// <summary>The picture at comparison size: z-scored grey over its opaque pixels, minus the count corner.</summary>
    (float[] Ref, bool[] Mask, int N)? Reference(string iconPath)
    {
        if (_refs.TryGetValue(iconPath, out var cached)) return cached;
        var icon = _icons.Get(iconPath);
        (float[], bool[], int)? made = null;
        if (icon is not null)
        {
            var rgba = new float[Size * Size * 4];
            new Plane(icon.Width, icon.Height, 4, icon.Rgba).ResampleInto(0, 0, icon.Width, icon.Height, Size, Size, rgba, null);
            var mask = new bool[Size * Size];
            var vals = new List<float>();
            for (int yy = 0; yy < Size; yy++)
                for (int xx = 0; xx < Size; xx++)
                {
                    int i = yy * Size + xx;
                    bool corner = yy < (int)(Size * 0.35) && xx < (int)(Size * 0.5);
                    mask[i] = rgba[i * 4 + 3] > 128 && !corner;
                    if (mask[i]) vals.Add((rgba[i * 4] + rgba[i * 4 + 1] + rgba[i * 4 + 2]) / 3f);
                }
            if (vals.Count > 200)
            {
                double m = vals.Average(), sd = Math.Sqrt(vals.Sum(v => (v - m) * (v - m)) / vals.Count) + 1e-6;
                made = (vals.Select(v => (float)((v - m) / sd)).ToArray(), mask, vals.Count);
            }
        }
        // Only remember a picture we actually got, so a slow download is retried next time.
        if (icon is not null) _refs[iconPath] = made;
        return made;
    }
}
