using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EwCapture.Core;

namespace EwCapture.Tests;

public class SignedFeedTests
{
    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly string Pub = Key.ExportSubjectPublicKeyInfoPem();
    static readonly byte[] Feed = Encoding.UTF8.GetBytes("""{"Assets":[{"FileName":"x-1.0.0-full.nupkg","SHA256":"ab"}]}""");

    static byte[] Sign(byte[] data, ECDsa key) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    static byte[] FreshMsg(long at, byte[] feed) => Encoding.UTF8.GetBytes(at.ToString()).Concat(feed).ToArray();

    // The release script's envelope: feed + sig, plus a signedAt bound to the feed by freshSig.
    static byte[] Envelope(byte[] feed, byte[] sig, long? signedAt = null, byte[]? freshSig = null)
    {
        var at = signedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fresh = freshSig ?? Sign(FreshMsg(at, feed), Key);
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> {
            ["feed"] = Convert.ToBase64String(feed),
            ["sig"] = Convert.ToBase64String(sig),
            ["signedAt"] = at.ToString(),
            ["freshSig"] = Convert.ToBase64String(fresh),
        });
    }

    [Fact]
    public void ShipsWithTheUpdateKey()
    {
        // Without it every update is refused, and installed copies could never update again.
        Assert.NotNull(SignedFeed.PublicKeyPem);
        using var key = ECDsa.Create();
        key.ImportFromPem(SignedFeed.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void OpensAFeedSignedWithTheKey() =>
        Assert.Equal(Feed, SignedFeed.Open(Envelope(Feed, Sign(Feed, Key)), Pub));

    [Fact]
    public void RefusesAChangedFeed()
    {
        var other = Encoding.UTF8.GetBytes("""{"Assets":[{"FileName":"evil-9.9.9-full.nupkg","SHA256":"cd"}]}""");
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Envelope(other, Sign(Feed, Key)), Pub));
    }

    [Fact]
    public void RefusesAnotherKeysSignature()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Envelope(Feed, Sign(Feed, stranger)), Pub));
    }

    [Fact]
    public void RefusesAFeedWithoutFreshness()
    {
        // A pre-freshness envelope (feed + sig only) is how a replay would arrive: refuse it.
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> {
            ["feed"] = Convert.ToBase64String(Feed), ["sig"] = Convert.ToBase64String(Sign(Feed, Key)) });
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(legacy, Pub));
    }

    [Fact]
    public void RefusesAStaleFeed()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-200).ToUnixTimeSeconds();
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Envelope(Feed, Sign(Feed, Key), old), Pub));
    }

    [Fact]
    public void RefusesAFeedSignedInTheFuture()
    {
        var ahead = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds();
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Envelope(Feed, Sign(Feed, Key), ahead), Pub));
    }

    [Fact]
    public void RefusesAReStampedTimestamp()
    {
        // An attacker with an old, validly-signed feed changes signedAt to "now" but can't
        // re-sign freshSig (no private key), so the freshness signature no longer matches.
        var old = DateTimeOffset.UtcNow.AddDays(-200).ToUnixTimeSeconds();
        var nowClaim = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var staleFresh = Sign(FreshMsg(old, Feed), Key); // signed for the real (old) time
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Envelope(Feed, Sign(Feed, Key), nowClaim, staleFresh), Pub));
    }

    [Fact]
    public void AcceptsAFeedWithinTheFreshnessWindow()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        Assert.Equal(Feed, SignedFeed.Open(Envelope(Feed, Sign(Feed, Key), recent), Pub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"feed":"e30="}""")]
    [InlineData("""{"feed":"!!","sig":"!!"}""")]
    [InlineData("""{"Assets":[]}""")]
    public void RefusesAnythingElse(string body) =>
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Encoding.UTF8.GetBytes(body), Pub));
}
