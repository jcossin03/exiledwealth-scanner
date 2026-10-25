using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EwCapture.Core;

/// <summary>
/// The update feed, signed. The release script wraps releases.win.json as
/// {"feed": base64, "sig": base64, "signedAt": unix-seconds, "freshSig": base64} in
/// releases.win-signed.json. Both signatures are ECDSA P-256 over SHA-256 (openssl's
/// DER form): <c>sig</c> covers the feed bytes, and <c>freshSig</c> covers signedAt
/// concatenated with the feed, so the release time is bound to this exact feed and a
/// replayed old feed can't be re-stamped with a current time without the private key.
/// The scanner trusts no feed this doesn't open, and refuses one older than
/// <see cref="MaxFeedAge"/> — a network attacker can't pin a client to a stale feed
/// to withhold a newer release indefinitely. The feed lists every package's SHA-256
/// and Velopack checks each download against it, so the signature covers the whole release.
/// One file, not feed + .sig: the edge cache could otherwise pair a new feed with an old signature.
/// (sig stays so scanners from before freshness, 0.4.3–0.4.11, still open the feed; they
/// ignore the extra fields. Those built with freshness require signedAt + freshSig.)
/// </summary>
public static class SignedFeed
{
    public const string FileName = "releases.win-signed.json";

    /// <summary>A feed signed longer ago than this is refused as a possible replay.</summary>
    public static readonly TimeSpan MaxFeedAge = TimeSpan.FromDays(90);
    static readonly TimeSpan MaxClockSkew = TimeSpan.FromDays(2);

    /// <summary>The public half of the update key (tools/scanner-update-key.sh), or null in a build without one.</summary>
    public static string? PublicKeyPem { get; } = LoadKey();

    /// <summary>The feed's bytes, or a CryptographicException if anything is off.</summary>
    public static byte[] Open(ReadOnlySpan<byte> envelope, string publicKeyPem, DateTimeOffset? now = null)
    {
        Envelope? e;
        try { e = JsonSerializer.Deserialize<Envelope>(envelope); }
        catch (JsonException) { throw new CryptographicException("signed feed: not JSON"); }
        if (e?.Feed is null || e.Sig is null) throw new CryptographicException("signed feed: no feed or signature");
        if (e.SignedAt is null || e.FreshSig is null) throw new CryptographicException("signed feed: no freshness");
        byte[] feed, sig, freshSig;
        try { feed = Convert.FromBase64String(e.Feed); sig = Convert.FromBase64String(e.Sig); freshSig = Convert.FromBase64String(e.FreshSig); }
        catch (FormatException) { throw new CryptographicException("signed feed: not base64"); }
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (!key.VerifyData(feed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("signed feed: bad signature");
        // freshSig covers signedAt + feed: a replayed feed can't claim a time it wasn't signed for.
        if (!key.VerifyData(FreshMessage(e.SignedAt, feed), freshSig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("signed feed: bad freshness signature");
        if (!long.TryParse(e.SignedAt, out var secs)) throw new CryptographicException("signed feed: bad signedAt");
        var when = DateTimeOffset.FromUnixTimeSeconds(secs);
        var n = now ?? DateTimeOffset.UtcNow;
        if (when > n + MaxClockSkew) throw new CryptographicException("signed feed: signed in the future");
        if (when < n - MaxFeedAge) throw new CryptographicException("signed feed: too old (possible replay)");
        return feed;
    }

    /// <summary>The bytes freshSig signs: the signedAt string followed by the feed.</summary>
    static byte[] FreshMessage(string signedAt, byte[] feed)
    {
        var at = Encoding.UTF8.GetBytes(signedAt);
        var msg = new byte[at.Length + feed.Length];
        Buffer.BlockCopy(at, 0, msg, 0, at.Length);
        Buffer.BlockCopy(feed, 0, msg, at.Length, feed.Length);
        return msg;
    }

    static string? LoadKey()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EwCapture.Core.update-key.pem");
        return s is null ? null : new StreamReader(s).ReadToEnd();
    }

    sealed record Envelope(
        [property: JsonPropertyName("feed")] string? Feed,
        [property: JsonPropertyName("sig")] string? Sig,
        [property: JsonPropertyName("signedAt")] string? SignedAt,
        [property: JsonPropertyName("freshSig")] string? FreshSig);
}
