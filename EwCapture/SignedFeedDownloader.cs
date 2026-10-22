using System.Security.Cryptography;
using System.Text;
using EwCapture.Core;
using Velopack.Sources;

namespace EwCapture;

/// <summary>
/// Velopack's downloader, except that a request for the feed fetches the signed
/// feed instead and hands Velopack only what <see cref="SignedFeed"/> verified.
/// Packages download as usual: Velopack checks each against the feed's SHA-256.
/// </summary>
sealed class SignedFeedDownloader : IFileDownloader
{
    readonly HttpClientFileDownloader _inner = new();

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        var u = new UriBuilder(url);
        var slash = u.Path.LastIndexOf('/') + 1;
        var name = u.Path[slash..];
        if (!name.StartsWith("releases.", StringComparison.OrdinalIgnoreCase)) return await _inner.DownloadBytes(url, headers, timeout);
        if (name != "releases.win.json") throw new CryptographicException($"unexpected feed {name}");
        u.Path = u.Path[..slash] + SignedFeed.FileName; // the query (Velopack's localVersion=…) stays
        var key = SignedFeed.PublicKeyPem ?? throw new CryptographicException("this build has no update key");
        return SignedFeed.Open(await _inner.DownloadBytes(u.Uri.ToString(), headers, timeout), key);
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));

    public Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        => _inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);
}
