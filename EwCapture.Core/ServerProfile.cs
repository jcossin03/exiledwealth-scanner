using System.Text.Json;

namespace EwCapture.Core;

/// <summary>
/// The server this install talks to (the live site or dev) and everything that follows from it. It's worked
/// out once at start-up by <see cref="Resolve"/> and handed to whatever needs it, never re-derived. Each
/// server has its own device token and queues, so a read taken while pointed at dev never reaches the live site.
/// <para>
/// The names on disk predate this type and must stay as they are, or existing installs lose their link or
/// their queued reads. They look inverted, and that's on purpose: live keeps the original queue folders (in
/// the data folder itself) but its token is token-exiledwealth.com.bin; dev keeps the original token.bin but
/// its queues sit in a dev.exiledwealth.com folder.
/// </para>
/// </summary>
/// <param name="BaseUrl">The site, ending in a slash: the API and the update feed live under it.</param>
/// <param name="IsDev">The developer's test server: diagnostic screenshots are taken only there.</param>
/// <param name="DataDir">The install's data folder (%LocalAppData%\ExiledWealth), shared by both servers.</param>
/// <param name="TokenFile">This server's device token file.</param>
/// <param name="QueuesDir">The folder this server's queues sit in.</param>
public sealed record ServerProfile(Uri BaseUrl, bool IsDev, string DataDir, string TokenFile, string QueuesDir)
{
    public const string Live = "https://exiledwealth.com/", Dev = "https://dev.exiledwealth.com/";

    /// <summary>The file the tray's Server menu writes, in the data folder; absent, appsettings.json decides.</summary>
    public const string ChoiceFileName = "server.txt";

    /// <summary>The servers an install may point at, nothing else, so no file can redirect uploads elsewhere.</summary>
    public static IReadOnlyList<string> Servers => [Live, Dev];

    public string Host => BaseUrl.Host;

    /// <summary>The reads waiting to go up (<see cref="SnapshotQueue"/>).</summary>
    public string SnapshotsDir => Path.Combine(QueuesDir, "snapshots");

    /// <summary>Dev's diagnostic screenshots waiting to go up (<see cref="UploadQueue"/>).</summary>
    public string CapturesDir => Path.Combine(QueuesDir, "queue");

    /// <summary>Waystone and tablet copies waiting to go up (<see cref="ItemCopyQueue"/>).</summary>
    public string ItemCopiesDir => Path.Combine(QueuesDir, "item-copies");

    public string ChoiceFile => Path.Combine(DataDir, ChoiceFileName);

    /// <summary>
    /// The server to use: the one in server.txt (<paramref name="chosen"/>, the file's text or null when it's
    /// missing), else appsettings.json's baseUrl, else the live site. Either must be one of <see cref="Servers"/>.
    /// <paramref name="warning"/> is a line to log when appsettings.json names some other server.
    /// </summary>
    public static ServerProfile Resolve(string dataDir, string? chosen, string? appSettingsJson, out string? warning)
    {
        warning = null;
        var url = chosen?.Trim() is { } c && Servers.Contains(c) ? c : FromAppSettings(appSettingsJson, out warning);
        return For(url, dataDir);
    }

    /// <summary>The profile for one of <see cref="Servers"/>, with the file and folder names existing installs use.</summary>
    public static ServerProfile For(string url, string dataDir)
    {
        if (!Servers.Contains(url)) throw new ArgumentException($"{url} isn't one of ours", nameof(url));
        var uri = new Uri(url);
        var dev = url == Dev;
        return new(uri, dev, dataDir,
            Path.Combine(dataDir, dev ? "token.bin" : $"token-{uri.Host}.bin"),
            dev ? Path.Combine(dataDir, uri.Host) : dataDir);
    }

    static string FromAppSettings(string? json, out string? warning)
    {
        warning = null;
        string? url = null;
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("baseUrl", out var v) && v.GetString() is { } s)
                    url = s.EndsWith('/') ? s : s + "/";
            }
            // Not JSON, not an object, or a baseUrl that isn't a string: as if there were none.
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }
        if (url is null) return Live;
        if (Servers.Contains(url)) return url;
        warning = $"appsettings.json names {url}, which isn't one of ours: using {Live}";
        return Live;
    }
}
