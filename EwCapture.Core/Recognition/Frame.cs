namespace EwCapture.Core.Recognition;

/// <summary>
/// A captured client area as raw 32-bit BGRA pixels, top row first. Core stays
/// free of image libraries: the exe hands over the bitmap's bits, tests decode PNGs.
/// </summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public Frame(int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("buffer size doesn't match width x height x 4");
        (Width, Height, Bgra) = (width, height, bgra);
    }

    /// <summary>Luma as the prototype measured it (ITU-R 601, like PIL's "L").</summary>
    public float[] Gray()
    {
        var g = new float[Width * Height];
        for (int i = 0, p = 0; i < g.Length; i++, p += 4)
            g[i] = Bgra[p + 2] * 0.299f + Bgra[p + 1] * 0.587f + Bgra[p] * 0.114f;
        return g;
    }

    /// <summary>
    /// The left part of the frame rescaled so the client area is 1440 pixels high,
    /// the height every layout coordinate refers to. The stash panel is anchored
    /// left, so only that strip is kept.
    /// </summary>
    public Frame NormalisedTo1440(int keepWidthAt1440 = 1100)
    {
        if (Height == 1440 && Width <= keepWidthAt1440) return this;
        double s = Height / 1440.0;
        int outW = Math.Min(keepWidthAt1440, (int)Math.Floor(Width / s));
        if (Height == 1440) return Crop(0, 0, outW, 1440);
        var src = new Plane(Width, Height, 4, Bgra);
        return new Frame(outW, 1440, src.Resample(0, 0, outW * s, Height, outW, 1440));
    }

    /// <summary>The whole frame resampled to w x h.</summary>
    public Frame Resized(int w, int h) =>
        w == Width && h == Height ? this : new Frame(w, h, new Plane(Width, Height, 4, Bgra).Resample(0, 0, Width, Height, w, h));

    Frame Crop(int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int r = 0; r < h; r++)
            Buffer.BlockCopy(Bgra, ((y + r) * Width + x) * 4, dst, r * w * 4, w * 4);
        return new Frame(w, h, dst);
    }
}

/// <summary>Area-averaging resampler over interleaved channels (box filter with fractional edges).</summary>
internal readonly struct Plane(int width, int height, int channels, byte[] data)
{
    /// <summary>Resample the source rectangle (sx, sy, sw, sh) to dw x dh.</summary>
    public byte[] Resample(double sx, double sy, double sw, double sh, int dw, int dh)
    {
        var f = new float[dw * dh * channels];
        ResampleInto(sx, sy, sw, sh, dw, dh, f, null);
        var o = new byte[f.Length];
        for (int i = 0; i < f.Length; i++) o[i] = (byte)Math.Clamp(Math.Round(f[i]), 0, 255);
        return o;
    }

    public void ResampleInto(double sx, double sy, double sw, double sh, int dw, int dh, float[] dst, float[]? srcFloat)
    {
        double fx = sw / dw, fy = sh / dh;
        for (int oy = 0; oy < dh; oy++)
        {
            double y0 = sy + oy * fy, y1 = y0 + fy;
            for (int ox = 0; ox < dw; ox++)
            {
                double x0 = sx + ox * fx, x1 = x0 + fx;
                for (int c = 0; c < channels; c++) dst[(oy * dw + ox) * channels + c] = 0;
                double total = 0;
                for (int yy = (int)Math.Floor(y0); yy < Math.Ceiling(y1); yy++)
                {
                    double wy = Math.Min(y1, yy + 1) - Math.Max(y0, yy);
                    if (wy <= 0 || yy < 0 || yy >= height) continue;
                    for (int xx = (int)Math.Floor(x0); xx < Math.Ceiling(x1); xx++)
                    {
                        double wx = Math.Min(x1, xx + 1) - Math.Max(x0, xx);
                        if (wx <= 0 || xx < 0 || xx >= width) continue;
                        double wgt = wx * wy; total += wgt;
                        int si = (yy * width + xx) * channels;
                        for (int c = 0; c < channels; c++)
                            dst[(oy * dw + ox) * channels + c] += (float)(wgt * (srcFloat is null ? data[si + c] : srcFloat[si + c]));
                    }
                }
                if (total > 0)
                    for (int c = 0; c < channels; c++) dst[(oy * dw + ox) * channels + c] /= (float)total;
            }
        }
    }
}
