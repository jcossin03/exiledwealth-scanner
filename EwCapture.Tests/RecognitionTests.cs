using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EwCapture.Core.Recognition;
using SkiaSharp;

namespace EwCapture.Tests;

/// <summary>
/// Golden tests: the C# recogniser must read the user's real captures exactly as
/// the prototype did (six of them checked slot by slot by eye). The captures are
/// the user's own and stay out of git: they live in scanner/fixtures (or EW_FIXTURES).
/// Item pictures come from GGG's CDN, cached in fixtures/icons like the exe caches them.
/// </summary>
public class RecognitionTests
{
    static readonly string ScannerDir = FindScannerDir();
    static readonly string Fixtures = Environment.GetEnvironmentVariable("EW_FIXTURES") ?? Path.Combine(ScannerDir, "fixtures");
    static readonly LayoutSet Layouts = LayoutSet.Parse(File.ReadAllText(Path.Combine(ScannerDir, "..", "worker", "data", "layouts.json")));
    static readonly Lazy<StashReader> Reader = new(() => new StashReader(Layouts, new CdnIcons(Path.Combine(Fixtures, "icons")), CountReader.FromEmbedded()));

    static string FindScannerDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "EwCapture.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("scanner/ not found above the test binaries");
    }

    public static IEnumerable<object[]> Goldens() =>
        Directory.GetFiles(Path.Combine(ScannerDir, "EwCapture.Tests", "golden"), "*.json").Order().Select(p => new object[] { Path.GetFileName(p) });

    sealed record GoldenRead(int Slot, string Item, int Count);
    sealed record Golden(string Capture, string Layout, bool CheckedByEye, GoldenRead[] Reads);

    [Theory]
    [MemberData(nameof(Goldens))]
    public void ReadsTheCaptureLikeTheVerifiedPrototype(string goldenFile)
    {
        var golden = JsonSerializer.Deserialize<Golden>(File.ReadAllText(Path.Combine(ScannerDir, "EwCapture.Tests", "golden", goldenFile)),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        var capture = Path.Combine(Fixtures, golden.Capture);
        Assert.True(File.Exists(capture), $"fixture {capture} missing: copy the captures into scanner/fixtures or set EW_FIXTURES");

        var result = Reader.Value.Read(LoadFrame(capture));

        Assert.NotNull(result);
        Assert.Equal(golden.Layout, result.Tab.Layout);
        var got = result.Reads.Select(r => $"{r.Slot}:{r.Item}:{r.Count}").ToList();
        var want = golden.Reads.Select(r => $"{r.Slot}:{r.Item}:{r.Count}").ToList();
        Assert.Equal(want, got);
    }

    [Theory]
    [InlineData("743", 743)]
    [InlineData("39.2K", 39200)]
    [InlineData("1K", 1000)]
    [InlineData("", null)]
    [InlineData("3.", null)]
    [InlineData("K", null)]
    [InlineData("1.2.3", null)]
    public void CountTextBecomesANumber(string text, int? expected) => Assert.Equal(expected, CountReader.Value(text));

    [Fact]
    public void NonFixedTabsAreNotForcedIntoALayout()
    {
        // Gems, Tablets, Trials and Wombgifts captures: no fixed layout fits them.
        foreach (var id in new[] { "01a4685c", "d3d7e278", "85905b51", "85eb0aeb", "a7e9015a" })
        {
            var file = Directory.GetFiles(Fixtures, id + "*.png").Single();
            Assert.Null(Reader.Value.Read(LoadFrame(file)));
        }
    }

    internal static Frame LoadFrame(string png)
    {
        using var bmp = SKBitmap.Decode(png);
        using var bgra = bmp.Copy(SKColorType.Bgra8888);
        return new Frame(bgra.Width, bgra.Height, bgra.Bytes);
    }

    /// <summary>Item pictures from GGG's CDN, cached on disk by a hash of their path.</summary>
    sealed class CdnIcons(string dir) : IIconSource
    {
        static readonly HttpClient Http = new() { BaseAddress = new Uri("https://web.poecdn.com"), Timeout = TimeSpan.FromSeconds(30) };

        public IconImage? Get(string path)
        {
            var file = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))) + ".png");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(dir);
                using var res = Http.GetAsync(path).GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) return null;
                File.WriteAllBytes(file, res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            }
            using var bmp = SKBitmap.Decode(file);
            using var rgba = bmp.Copy(SKColorType.Rgba8888);
            // Skia keeps colours premultiplied by alpha; the reference wants them straight.
            var px = rgba.Bytes;
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a is > 0 and < 255)
                    for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
            }
            return new IconImage(rgba.Width, rgba.Height, px);
        }
    }
}
