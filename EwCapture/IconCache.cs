using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>
/// Item pictures fetched from GGG's own image server and kept on this PC. Nothing
/// of GGG's ships with the app: each player's PC downloads what it needs, the
/// same images the official site and poe.ninja load.
/// </summary>
sealed class IconCache : IIconSource
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://web.poecdn.com"), Timeout = TimeSpan.FromSeconds(20) };
    const int MaxIconBytes = 512 * 1024;
    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    readonly string _dir;
    readonly ConcurrentDictionary<string, IconImage> _loaded = new();
    readonly Action<string> _log;

    static IconCache() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("ew-capture (+https://exiledwealth.com)");

    public IconCache(string dir, Action<string> log) => (_dir, _log) = (dir, log);

    string FileFor(string path) => Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))) + ".png");

    /// <summary>Download every picture the layouts name, a few at a time, so the first read is quick.</summary>
    public async Task PrefetchAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        int fetched = 0, failed = 0;
        await Parallel.ForEachAsync(paths.Distinct().Where(p => !File.Exists(FileFor(p))),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (path, token) =>
            {
                try
                {
                    // Only pictures on GGG's image server, whatever the layouts file says.
                    if (!Uri.TryCreate(Http.BaseAddress, path, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != Http.BaseAddress!.Host)
                    { Interlocked.Increment(ref failed); return; }
                    var bytes = await Http.GetByteArrayAsync(uri, token);
                    // A PNG of sensible size, nothing else reaches the image decoder.
                    if (bytes.Length > MaxIconBytes || !bytes.AsSpan().StartsWith(PngSignature)) { Interlocked.Increment(ref failed); return; }
                    await File.WriteAllBytesAsync(FileFor(path), bytes, token);
                    Interlocked.Increment(ref fetched);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { Interlocked.Increment(ref failed); }
            });
        if (fetched + failed > 0) _log($"item pictures: {fetched} downloaded, {failed} failed");
    }

    public IconImage? Get(string path)
    {
        if (_loaded.TryGetValue(path, out var hit)) return hit;
        var file = FileFor(path);
        if (!File.Exists(file)) return null; // not downloaded yet: the slot is skipped this time
        if (new FileInfo(file).Length > MaxIconBytes) { File.Delete(file); return null; }
        try
        {
            using var bmp = new Bitmap(file);
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[bmp.Width * bmp.Height * 4];
            try { for (int y = 0; y < bmp.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, px, y * bmp.Width * 4, bmp.Width * 4); }
            finally { bmp.UnlockBits(bits); }
            // GDI+ hands over straight (not premultiplied) BGRA; the recogniser wants RGBA.
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return _loaded[path] = new IconImage(bmp.Width, bmp.Height, px);
        }
        catch (Exception e) when (e is ArgumentException or IOException or ExternalException)
        {
            File.Delete(file); // a broken download; fetched again next time
            return null;
        }
    }
}
