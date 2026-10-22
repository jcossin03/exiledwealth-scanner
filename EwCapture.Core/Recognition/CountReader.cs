using System.Text.Json;
using System.Text.RegularExpressions;

namespace EwCapture.Core.Recognition;

/// <summary>
/// Reads a slot's stack count: white digits with a dark outline in its top-left
/// corner. Greedy left-to-right template match; the first glyph may start anywhere
/// in the corner, later ones follow closely. Big stacks are shortened ("39.2K").
/// Templates (0-9, ".", "K") were cut from our own captures.
/// </summary>
public sealed partial class CountReader
{
    const int BandTop = 2, BandRows = 22, BandCols = 62;
    const double MinGlyph = 0.75;

    readonly (string Glyph, int W, float[] Ink)[] _templates;

    public CountReader(string templatesJson)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, TemplateJson>>(templatesJson)
                  ?? throw new JsonException("no templates");
        _templates = raw.Select(kv => (kv.Key, kv.Value.w, kv.Value.ink)).ToArray();
        if (_templates.Any(t => t.Ink.Length != t.W * BandRows)) throw new JsonException("template size mismatch");
    }

    public static CountReader FromEmbedded()
    {
        using var s = typeof(CountReader).Assembly.GetManifestResourceStream("EwCapture.Core.digits.json")
                      ?? throw new InvalidOperationException("digits.json not embedded");
        return new CountReader(new StreamReader(s).ReadToEnd());
    }

    sealed record TemplateJson(int w, int h, float[] ink);

    /// <summary>The count in the slot whose top-left is (x, y) on a 1440p frame, or null.</summary>
    public (int Count, double Confidence)? Read(Frame f, int x, int y)
    {
        (string Text, double Conf) best = ("", 0);
        // The detected slot can sit a few pixels off the digits: take the row offset that reads best.
        for (int dy = -8; dy <= 8; dy++)
        {
            var r = ReadAt(f, x, y + dy);
            if (r.Text.Length > best.Text.Length || (r.Text.Length == best.Text.Length && r.Conf > best.Conf)) best = r;
        }
        var v = Value(best.Text);
        return v is null ? null : (v.Value, best.Conf);
    }

    (string Text, double Conf) ReadAt(Frame f, int x, int y)
    {
        var band = Band(f, x, y);
        int pos = 2;
        var text = "";
        double conf = 1;
        while (pos < BandCols - 4)
        {
            (double Score, string Glyph, int End)? best = null;
            int maxShift = text.Length == 0 ? 10 : 3;
            for (int shift = 0; shift <= maxShift; shift++)
                foreach (var (glyph, w, ink) in _templates)
                {
                    int p = pos + shift;
                    if (p + w > BandCols) continue;
                    double sc = Ncc(band, p, w, ink);
                    if (best is null || sc > best.Value.Score) best = (sc, glyph, p + w);
                }
            if (best is null || best.Value.Score < MinGlyph) break;
            text += best.Value.Glyph;
            pos = best.Value.End - 1;
            conf = Math.Min(conf, best.Value.Score);
            if (text.Length >= 5) break;
        }
        return (text, conf);
    }

    /// <summary>"Ink" per pixel of the count corner: bright and unsaturated, 0..1.</summary>
    static float[] Band(Frame f, int x, int y)
    {
        var band = new float[BandRows * BandCols];
        for (int r = 0; r < BandRows; r++)
        {
            int yy = y + BandTop + r;
            if (yy < 0 || yy >= f.Height) continue;
            for (int c = 0; c < BandCols; c++)
            {
                int xx = x + c;
                if (xx < 0 || xx >= f.Width) continue;
                int p = (yy * f.Width + xx) * 4;
                int b = f.Bgra[p], g = f.Bgra[p + 1], rr = f.Bgra[p + 2];
                int mn = Math.Min(rr, Math.Min(g, b)), mx = Math.Max(rr, Math.Max(g, b));
                band[r * BandCols + c] = mx - mn < 50 ? Math.Clamp((mn - 120) / 120f, 0, 1) : 0;
            }
        }
        return band;
    }

    /// <summary>Zero-mean normalised correlation of the band's columns [p, p+w) with a template.</summary>
    static double Ncc(float[] band, int p, int w, float[] t)
    {
        int n = BandRows * w;
        double ma = 0, mb = 0;
        for (int r = 0; r < BandRows; r++)
            for (int c = 0; c < w; c++) { ma += band[r * BandCols + p + c]; mb += t[r * w + c]; }
        ma /= n; mb /= n;
        double ab = 0, aa = 0, bb = 0;
        for (int r = 0; r < BandRows; r++)
            for (int c = 0; c < w; c++)
            {
                double a = band[r * BandCols + p + c] - ma, b = t[r * w + c] - mb;
                ab += a * b; aa += a * a; bb += b * b;
            }
        double den = Math.Sqrt(aa * bb);
        return ab / (den == 0 ? 1 : den);
    }

    [GeneratedRegex(@"^(\d+(?:\.\d)?)(K?)$")]
    private static partial Regex CountText();

    /// <summary>"39.2K" -> 39200, "743" -> 743; null for anything malformed.</summary>
    public static int? Value(string text)
    {
        var m = CountText().Match(text);
        if (!m.Success) return null;
        double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (int)Math.Round(m.Groups[2].Value == "K" ? v * 1000 : v);
    }
}
