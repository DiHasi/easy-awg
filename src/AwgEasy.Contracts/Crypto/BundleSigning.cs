using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AwgEasy.Contracts;

/// <summary>
/// Signing and verification for <see cref="SignedBundle"/>.
///
/// ECDSA P-256 over SHA-256 rather than Ed25519: .NET 10 has no Ed25519 in the BCL, and pulling
/// a third-party crypto library into the AOT-published agent (deployed on every node) costs more
/// than it buys here. P-256 is in the BCL, AOT-clean and hardware-accelerated.
///
/// Signatures use the fixed-field IEEE P-1363 encoding so they are constant length and carry no
/// DER parsing surface.
/// </summary>
public static class BundleSigning
{
    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDsa key) => Base64Url.Encode(key.ExportSubjectPublicKeyInfo());

    public static string ExportPrivateKey(ECDsa key) => Base64Url.Encode(key.ExportPkcs8PrivateKey());

    public static ECDsa ImportPublicKey(string base64UrlSpki)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Base64Url.Decode(base64UrlSpki), out _);
        return key;
    }

    public static ECDsa ImportPrivateKey(string base64UrlPkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Base64Url.Decode(base64UrlPkcs8), out _);
        return key;
    }

    /// <summary>Stable identifier for a public key, so bundles can name the key that signed them.</summary>
    public static string ComputeKeyId(ECDsa key)
        => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()).AsSpan(0, 8)).ToLowerInvariant();

    public static SignedBundle Sign(DesiredStateBundle bundle, ECDsa signingKey, string keyId, JsonTypeInfo<DesiredStateBundle> typeInfo)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, typeInfo);
        var payload = Base64Url.Encode(json);
        var signature = signingKey.SignData(Encoding.ASCII.GetBytes(payload), Hash, SignatureFormat);
        return new SignedBundle(payload, Base64Url.Encode(signature), keyId);
    }

    /// <summary>
    /// Verifies the envelope and returns the decoded bundle. The signature is checked against the
    /// payload exactly as received: the bundle is only deserialized after the bytes are proven
    /// authentic, never re-serialized for verification.
    /// </summary>
    public static bool TryVerify(
        SignedBundle? envelope,
        ECDsa publicKey,
        string expectedKeyId,
        JsonTypeInfo<DesiredStateBundle> typeInfo,
        out DesiredStateBundle bundle,
        out ApiError error)
    {
        bundle = default!;
        error = ApiError.Empty;

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            error = new ApiError("bundle_envelope_invalid", "Signed bundle is missing payload or signature.");
            return false;
        }

        if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
        {
            error = new ApiError(
                "bundle_key_mismatch",
                $"Bundle was signed with key '{envelope.KeyId}' but this node pinned '{expectedKeyId}'.");
            return false;
        }

        byte[] signature;
        byte[] json;
        try
        {
            signature = Base64Url.Decode(envelope.Signature);
            json = Base64Url.Decode(envelope.Payload);
        }
        catch (FormatException)
        {
            error = new ApiError("bundle_envelope_invalid", "Signed bundle is not valid base64url.");
            return false;
        }

        if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(envelope.Payload), signature, Hash, SignatureFormat))
        {
            error = new ApiError("bundle_signature_invalid", "Bundle signature does not verify against the pinned control plane key.");
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize(json, typeInfo);
            if (decoded is null)
            {
                error = new ApiError("bundle_envelope_invalid", "Bundle payload decoded to null.");
                return false;
            }

            bundle = decoded;
            return true;
        }
        catch (JsonException exception)
        {
            error = new ApiError("bundle_envelope_invalid", $"Bundle payload is not valid JSON: {exception.Message}");
            return false;
        }
    }
}
