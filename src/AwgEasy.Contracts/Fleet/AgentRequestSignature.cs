using System.Security.Cryptography;
using System.Text;

namespace AwgEasy.Contracts;

/// <summary>
/// Proof-of-possession for agent requests, shared by both ends so they cannot disagree on what
/// was signed.
///
/// The agent signs method, path, timestamp, nonce and a hash of the body with its own key. The
/// private key never crosses the wire - unlike a bearer token, which is replayable by anyone who
/// sees it in a log or a proxy. Unlike mTLS this also survives a TLS-terminating proxy or CDN in
/// front of the control plane, which matters for a panel that may want to hide its origin.
/// </summary>
public static class AgentRequestSignature
{
    public const string NodeHeader = "X-Awg-Node";
    public const string TimestampHeader = "X-Awg-Timestamp";
    public const string NonceHeader = "X-Awg-Nonce";
    public const string SignatureHeader = "X-Awg-Signature";

    /// <summary>How far a request timestamp may drift before it is refused as a possible replay.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    /// <summary>
    /// The body is covered as a hash rather than inline, so signing cost does not grow with a
    /// large status report and the canonical string stays a fixed shape.
    /// </summary>
    public static string BuildCanonicalRequest(string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var bodyHash = Base64Url.Encode(SHA256.HashData(body));
        return $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}\n{bodyHash}";
    }

    public static string Sign(ECDsa privateKey, string canonicalRequest)
        => Base64Url.Encode(privateKey.SignData(Encoding.UTF8.GetBytes(canonicalRequest), Hash, Format));

    public static bool Verify(ECDsa publicKey, string canonicalRequest, string signature)
    {
        byte[] decoded;
        try
        {
            decoded = Base64Url.Decode(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return publicKey.VerifyData(Encoding.UTF8.GetBytes(canonicalRequest), decoded, Hash, Format);
    }

    public static string NewNonce() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

    public static string Timestamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
