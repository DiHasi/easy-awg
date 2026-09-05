using System.Text.Json.Serialization.Metadata;
using AwgEasy.Contracts;

namespace AwgEasy.Tests;

public class BundleSigningTests
{
    private static readonly JsonTypeInfo<DesiredStateBundle> TypeInfo = ContractsJsonContext.Default.DesiredStateBundle;

    private static DesiredStateBundle Sample() => new(
        DesiredStateBundle.CurrentSchemaVersion,
        42,
        "node-a",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(10),
        new FleetIdentity(1, "server-private", "server-public"),
        new NetworkProfile("10.8.0.0/24", 51820),
        new ServerObfuscationProfile { S1 = 15, H1 = "1234567891" },
        new NodeSettings("awg0", null, 1420),
        [new BundlePeer("peer-key", "peer-psk", "10.8.0.2")]);

    [Fact]
    public void Round_trips_a_signed_bundle()
    {
        using var key = BundleSigning.CreateKey();
        var keyId = BundleSigning.ComputeKeyId(key);
        var signed = BundleSigning.Sign(Sample(), key, keyId, TypeInfo);

        using var publicKey = BundleSigning.ImportPublicKey(BundleSigning.ExportPublicKey(key));
        Assert.True(BundleSigning.TryVerify(signed, publicKey, keyId, TypeInfo, out var bundle, out var error), error.Message);
        Assert.Equal(42, bundle.Revision);
        Assert.Equal("10.8.0.2", Assert.Single(bundle.Peers).Address);
    }

    [Fact]
    public void Rejects_a_tampered_payload()
    {
        using var key = BundleSigning.CreateKey();
        var keyId = BundleSigning.ComputeKeyId(key);
        var signed = BundleSigning.Sign(Sample(), key, keyId, TypeInfo);
        var tampered = signed with { Payload = signed.Payload[..^2] + (signed.Payload[^2] == 'A' ? "BA" : "AA") };

        using var publicKey = BundleSigning.ImportPublicKey(BundleSigning.ExportPublicKey(key));
        Assert.False(BundleSigning.TryVerify(tampered, publicKey, keyId, TypeInfo, out _, out var error));
        Assert.Equal("bundle_signature_invalid", error.Code);
    }

    [Fact]
    public void Rejects_a_bundle_signed_by_a_different_key()
    {
        using var pinned = BundleSigning.CreateKey();
        using var rogue = BundleSigning.CreateKey();
        var keyId = BundleSigning.ComputeKeyId(pinned);
        var signed = BundleSigning.Sign(Sample(), rogue, keyId, TypeInfo);

        using var publicKey = BundleSigning.ImportPublicKey(BundleSigning.ExportPublicKey(pinned));
        Assert.False(BundleSigning.TryVerify(signed, publicKey, keyId, TypeInfo, out _, out var error));
        Assert.Equal("bundle_signature_invalid", error.Code);
    }

    // A rotated signing key must not be able to silently take over a node that pinned the old one.
    [Fact]
    public void Rejects_a_bundle_naming_a_key_the_node_did_not_pin()
    {
        using var key = BundleSigning.CreateKey();
        var signed = BundleSigning.Sign(Sample(), key, "some-other-key-id", TypeInfo);

        using var publicKey = BundleSigning.ImportPublicKey(BundleSigning.ExportPublicKey(key));
        Assert.False(BundleSigning.TryVerify(signed, publicKey, "pinned-key-id", TypeInfo, out _, out var error));
        Assert.Equal("bundle_key_mismatch", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    public void Base64Url_round_trips_every_padding_length(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        Assert.Equal(bytes, Base64Url.Decode(Base64Url.Encode(bytes)));
    }

    [Fact]
    public void Base64Url_output_is_url_safe_and_unpadded()
    {
        var encoded = Base64Url.Encode(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }
}
