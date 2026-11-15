using EwCapture.Core;

namespace EwCapture.Tests;

public class ServerProfileTests
{
    static readonly string Data = Path.Combine(Path.GetTempPath(), "ExiledWealth");

    [Fact]
    public void NoFilesMeansTheLiveSiteWithTheOriginalQueueFolders()
    {
        var p = ServerProfile.Resolve(Data, chosen: null, appSettingsJson: null, out var warning);
        Assert.Null(warning);
        Assert.Equal(new Uri("https://exiledwealth.com/"), p.BaseUrl);
        Assert.False(p.IsDev);
        // The names existing installs already have on disk: changing one loses a link or a queue.
        Assert.Equal(Path.Combine(Data, "token-exiledwealth.com.bin"), p.TokenFile);
        Assert.Equal(Data, p.QueuesDir);
        Assert.Equal(Path.Combine(Data, "snapshots"), p.SnapshotsDir);
        Assert.Equal(Path.Combine(Data, "queue"), p.CapturesDir);
        Assert.Equal(Path.Combine(Data, "server.txt"), p.ChoiceFile);
    }

    [Fact]
    public void DevKeepsThePlainTokenFileAndItsOwnQueueFolder()
    {
        var p = ServerProfile.Resolve(Data, "https://dev.exiledwealth.com/\r\n", null, out _);
        Assert.Equal(new Uri("https://dev.exiledwealth.com/"), p.BaseUrl);
        Assert.True(p.IsDev);
        Assert.Equal(Path.Combine(Data, "token.bin"), p.TokenFile);
        Assert.Equal(Path.Combine(Data, "dev.exiledwealth.com"), p.QueuesDir);
        Assert.Equal(Path.Combine(Data, "dev.exiledwealth.com", "snapshots"), p.SnapshotsDir);
        Assert.Equal(Path.Combine(Data, "dev.exiledwealth.com", "queue"), p.CapturesDir);
    }

    [Fact]
    public void TheMenuChoiceWinsOverAppSettings()
    {
        var p = ServerProfile.Resolve(Data, "https://exiledwealth.com/", """{"baseUrl":"https://dev.exiledwealth.com/"}""", out _);
        Assert.False(p.IsDev);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("https://evil.example/")]
    [InlineData("https://dev.exiledwealth.com")] // no slash: server.txt must match exactly, as it always had to
    public void AServerFileThatIsntOneOfOursFallsBackToAppSettings(string chosen)
    {
        Assert.True(ServerProfile.Resolve(Data, chosen, """{"baseUrl":"https://dev.exiledwealth.com"}""", out _).IsDev);
        Assert.False(ServerProfile.Resolve(Data, chosen, null, out _).IsDev);
    }

    [Fact]
    public void AppSettingsNamingAnotherServerIsLoggedAndIgnored()
    {
        var p = ServerProfile.Resolve(Data, null, """{"baseUrl":"https://evil.example"}""", out var warning);
        Assert.False(p.IsDev);
        Assert.Equal("appsettings.json names https://evil.example/, which isn't one of ours: using https://exiledwealth.com/", warning);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"baseUrl":42}""")]
    [InlineData("""{"other":"x"}""")]
    public void UnreadableAppSettingsMeanTheLiveSite(string json)
    {
        var p = ServerProfile.Resolve(Data, null, json, out var warning);
        Assert.False(p.IsDev);
        Assert.Null(warning);
    }

    [Fact]
    public void OnlyOurServersHaveAProfile() =>
        Assert.Throws<ArgumentException>(() => ServerProfile.For("https://evil.example/", Data));
}
