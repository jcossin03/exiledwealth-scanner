using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EwCapture.Core;

/// <summary>
/// The update feed, signed. The release script wraps releases.win.json as
/// {"feed": base64, "sig": base64} in releases.win-signed.json, the signature being
/// ECDSA P-256 over SHA-256 (openssl's DER form). The scanner trusts no feed this
/// doesn't open. The feed lists every package's SHA-256 and Velopack checks each
/// download against it, so the one signature covers the whole release.
/// One file, not feed + .sig: the edge cache could otherwise pair a new feed with an old signature.
/// No expiry, on purpose: 0.4.12 refused a feed signed over 90 days ago, so every copy's update
/// check would fail through any quiet spell, for no real gain (whoever could replay an old feed
/// could as well block it, and Velopack never downgrades). The release script still adds
/// signedAt + freshSig for 0.4.12; this ignores them.
/// </summary>
public static class SignedFeed
{
    public const string FileName = "releases.win-signed.json";

    /// <summary>The public half of the update key (tools/scanner-update-key.sh), or null in a build without one.</summary>
    public static string? PublicKeyPem { get; } = LoadKey();

    /// <summary>The feed's bytes, or a CryptographicException if anything is off.</summary>
    public static byte[] Open(ReadOnlySpan<byte> envelope, string publicKeyPem)
    {
        Envelope? e;
        try { e = JsonSerializer.Deserialize<Envelope>(envelope); }
        catch (JsonException) { throw new CryptographicException("signed feed: not JSON"); }
        if (e?.Feed is null || e.Sig is null) throw new CryptographicException("signed feed: no feed or signature");
        byte[] feed, sig;
        try { feed = Convert.FromBase64String(e.Feed); sig = Convert.FromBase64String(e.Sig); }
        catch (FormatException) { throw new CryptographicException("signed feed: not base64"); }
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (!key.VerifyData(feed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("signed feed: bad signature");
        return feed;
    }

    static string? LoadKey()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EwCapture.Core.update-key.pem");
        return s is null ? null : new StreamReader(s).ReadToEnd();
    }

    sealed record Envelope(
        [property: JsonPropertyName("feed")] string? Feed,
        [property: JsonPropertyName("sig")] string? Sig);
}
