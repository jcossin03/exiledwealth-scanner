using EwCapture.Core.Recognition;

namespace EwCapture.Tests;

public class TabStripTests
{
    static readonly string Fixtures = Environment.GetEnvironmentVariable("EW_FIXTURES")
        ?? Path.Combine(Path.GetDirectoryName(typeof(TabStripTests).Assembly.Location)!, "..", "..", "..", "..", "fixtures");

    // Where the open tab's label sits in each of the user's captures (checked by eye on the strip).
    [Theory]
    [InlineData("e45b7710", 62, 228)]   // Currency, leftmost
    [InlineData("34cd3d5d", 621, 771)]  // Essence, with Currency (same green) also visible
    [InlineData("35a8c9d1", 635, 785)]  // Sockets
    [InlineData("01a4685c", 660, 785)]  // Gems
    [InlineData("0d889cd6", 602, 785)]  // Fragments
    [InlineData("ee435c3c", 624, 785)]  // Delirium
    [InlineData("0d59b4b0", 606, 783)]  // Expedition: grey tab, edge broken by small gaps
    [InlineData("bdf25fb9", 648, 785)]  // Breach
    [InlineData("e74801d1", 661, 782)]  // Abyss: lime tab, edge broken by small gaps
    [InlineData("ba46b158", 653, 785)]  // Ritual
    public void FindsTheOpenTab(string capture, int x0, int x1)
    {
        var file = Directory.GetFiles(Fixtures, capture + "*.png").Single();
        var found = TabStrip.FindOpenTab(RecognitionTests.LoadFrame(file));
        Assert.NotNull(found);
        Assert.InRange(found.Value.X0, x0 - 5, x0 + 5);
        Assert.InRange(found.Value.X1, x1 - 5, x1 + 5);
    }

    [Theory]
    [InlineData("Currency", "Currency")]
    [InlineData("  Essence  ®", "Essence")]
    [InlineData("Map Sale", "Map Sale")]
    [InlineData("Fragments X", "Fragments")]
    [InlineData("2", "2")]
    [InlineData("Breach  2", "Breach 2")]
    public void CleansOcrNames(string raw, string clean) => Assert.Equal(clean, TabStrip.CleanName(raw));
}

public class StashGateTests
{
    static readonly string Fixtures = Environment.GetEnvironmentVariable("EW_FIXTURES")
        ?? Path.Combine(Path.GetDirectoryName(typeof(StashGateTests).Assembly.Location)!, "..", "..", "..", "..", "fixtures");

    static Frame Crop(Frame f, int x, int y, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Buffer.BlockCopy(f.Bgra, ((y + r) * f.Width + x) * 4, px, r * w * 4, w * 4);
        return new Frame(w, h, px);
    }

    [Fact]
    public void SeesTheStashOpenInEveryCaptureFromTheGateStripAlone()
    {
        foreach (var file in Directory.GetFiles(Fixtures, "*-*-*-*-*.png"))
        {
            var f = RecognitionTests.LoadFrame(file);
            var strip = Crop(f, 0, TabStrip.GateTop, TabStrip.GateWidth, TabStrip.GateRows);
            Assert.True(TabStrip.StashOpen(strip), Path.GetFileName(file));
        }
    }

    [Fact]
    public void TheGameWorldIsNotAStash()
    {
        // The same strip height taken from the middle of the screen (hideout, ground, effects)
        // and from lower down the stash panel itself.
        foreach (var file in Directory.GetFiles(Fixtures, "*-*-*-*-*.png"))
        {
            var f = RecognitionTests.LoadFrame(file);
            foreach (var (x, y) in new[] { (1300, 150), (1300, 600), (2200, 900), (0, 600), (0, 1000), (1100, 300) })
                Assert.False(TabStrip.StashOpen(Crop(f, x, y, TabStrip.GateWidth, TabStrip.GateRows), y), $"{Path.GetFileName(file)} at {x},{y}");
        }
    }
}

public class StripCoverTests
{
    static readonly string Fixtures = Environment.GetEnvironmentVariable("EW_FIXTURES")
        ?? Path.Combine(Path.GetDirectoryName(typeof(StripCoverTests).Assembly.Location)!, "..", "..", "..", "..", "fixtures");

    // These four were taken with a sub-tab's tooltip (Kalguuran Runes, Soul Cores, Idols,
    // Ancient Augments) over the tab strip; the scanner misread a name under one of them.
    [Theory]
    [InlineData("04ebbdd0", false)]
    [InlineData("f3084f80", false)]
    [InlineData("4ad875eb", false)]
    [InlineData("be2a5c06", false)]
    [InlineData("e45b7710", true)]
    [InlineData("35a8c9d1", true)]
    [InlineData("0d59b4b0", true)]
    [InlineData("ba46b158", true)]
    public void TrustsANameOnlyWhenNothingCoversTheStrip(string capture, bool uncovered)
    {
        var file = Directory.GetFiles(Fixtures, capture + "*.png").Single();
        Assert.Equal(uncovered, TabStrip.StripUncovered(RecognitionTests.LoadFrame(file)));
    }
}
