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

    static byte[] Envelope(byte[] feed, byte[] sig) => JsonSerializer.SerializeToUtf8Bytes(
        new Dictionary<string, string> { ["feed"] = Convert.ToBase64String(feed), ["sig"] = Convert.ToBase64String(sig) });

    static byte[] Sign(byte[] data, ECDsa key) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

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

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"feed":"e30="}""")]
    [InlineData("""{"feed":"!!","sig":"!!"}""")]
    [InlineData("""{"Assets":[]}""")]
    public void RefusesAnythingElse(string body) =>
        Assert.Throws<CryptographicException>(() => SignedFeed.Open(Encoding.UTF8.GetBytes(body), Pub));
}
