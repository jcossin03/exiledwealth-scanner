using System.Text.RegularExpressions;

namespace EwCapture.Core;

/// <summary>
/// Reads the settings that change how the stash renders (UI text size, language,
/// controller UI, display mode) from the game's production config file.
/// </summary>
public static class GameConfig
{
    // Allowlist, not blocklist: the file also holds login and network sections,
    // and nothing from those should ever leave the PC.
    static readonly Regex KeptSections = new(
        "^(DISPLAY|UI|GENERAL|LANGUAGE|GAMEPAD|CONTROLLER|ACCESSIBILITY)$", // whole names only: not GUILD or BUILD
        RegexOptions.IgnoreCase);

    /// <summary>
    /// The config file under Documents\My Games\Path of Exile 2, or null. The name
    /// differs between clients, so take any *production_Config.ini there.
    /// </summary>
    public static string? FindConfigFile(string documentsDir)
    {
        var dir = Path.Combine(documentsDir, "My Games", "Path of Exile 2");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*production_Config.ini")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Kept sections and their keys. Unreadable or missing file → empty.</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadRelevant(string? path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        if (path is null) return result;
        string text;
        try
        {
            // The game may hold the file open; share it rather than fail.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }
        return Parse(text);
    }

    public static Dictionary<string, Dictionary<string, string>> Parse(string ini)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                current = KeptSections.IsMatch(name) ? new Dictionary<string, string>() : null;
                if (current is not null) result[name] = current;
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }
}
