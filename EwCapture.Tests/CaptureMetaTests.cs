using System.Text.Json;
using EwCapture.Core;

namespace EwCapture.Tests;

public class CaptureMetaTests
{
    [Fact]
    public void SerialisesInTheShapeTheServerDocuments()
    {
        var meta = new CaptureMeta(
            "ew-capture 0.1.0",
            new DateTimeOffset(2026, 10, 20, 19, 2, 11, TimeSpan.Zero),
            null,
            new Size2(2560, 1440),
            new MonitorInfo(2560, 1440, 1.25),
            new GameInfo("PathOfExileSteam", "borderless", new() { ["UI"] = new() { ["ui_text_size"] = "1" } }));

        using var doc = JsonDocument.Parse(meta.ToJson());
        var root = doc.RootElement;
        Assert.Equal("ew-capture 0.1.0", root.GetProperty("tool").GetString());
        Assert.False(root.TryGetProperty("label", out _)); // null label is left out
        Assert.Equal(1440, root.GetProperty("client").GetProperty("height").GetInt32());
        Assert.Equal(1.25, root.GetProperty("monitor").GetProperty("dpiScale").GetDouble());
        Assert.Equal("borderless", root.GetProperty("game").GetProperty("windowMode").GetString());
        // Config keys are game data and must keep their own spelling.
        Assert.Equal("1", root.GetProperty("game").GetProperty("config").GetProperty("UI").GetProperty("ui_text_size").GetString());
    }
}
