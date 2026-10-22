using EwCapture.Core;

namespace EwCapture.Tests;

public class GameConfigTests
{
    const string Sample = """
        [LOGIN]
        account_name=SecretExile
        remember_me=true

        [DISPLAY]
        resolution_width=2560
        resolution_height=1440
        fullscreen=false
        borderless_windowed_fullscreen=true

        ; a comment
        [UI]
        ui_text_size=1
        use_gamepad_ui = true

        [NETWORK]
        last_gateway=Texas
        [LANGUAGE]
        language=en
        """;

    [Fact]
    public void KeepsOnlyRenderingSections()
    {
        var cfg = GameConfig.Parse(Sample);
        Assert.Equal(["DISPLAY", "UI", "LANGUAGE"], cfg.Keys.ToArray());
        Assert.Equal("2560", cfg["DISPLAY"]["resolution_width"]);
        Assert.Equal("true", cfg["UI"]["use_gamepad_ui"]); // spaces round '=' trimmed
    }

    [Fact]
    public void NeverKeepsLoginOrNetworkValues()
    {
        var all = GameConfig.Parse(Sample).Values.SelectMany(s => s.Values);
        Assert.DoesNotContain("SecretExile", all);
        Assert.DoesNotContain("Texas", all);
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        var cfg = GameConfig.Parse(Sample.Replace("\n", "\r\n"));
        Assert.Equal("1440", cfg["DISPLAY"]["resolution_height"]);
    }

    [Fact]
    public void MissingFileGivesEmptyConfig()
    {
        Assert.Empty(GameConfig.ReadRelevant(null));
        Assert.Empty(GameConfig.ReadRelevant(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini")));
    }

    [Fact]
    public void FindsTheConfigUnderMyGames()
    {
        var docs = Directory.CreateTempSubdirectory().FullName;
        Assert.Null(GameConfig.FindConfigFile(docs));
        var dir = Directory.CreateDirectory(Path.Combine(docs, "My Games", "Path of Exile 2")).FullName;
        File.WriteAllText(Path.Combine(dir, "poe2_production_Config.ini"), Sample);
        Assert.EndsWith("poe2_production_Config.ini", GameConfig.FindConfigFile(docs));
    }

    [Fact]
    public void KeepsOnlySectionsNamedExactly()
    {
        var cfg = GameConfig.Parse("[GUILD]\nsecret=1\n[BUILD_INFO]\nx=2\n[UI]\nscale=1\n[DISPLAYS_OLD]\ny=3\n");
        Assert.Equal(["UI"], cfg.Keys.ToArray());
    }
}
