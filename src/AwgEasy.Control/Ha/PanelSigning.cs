using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public static class PanelVersion
{
    public static string Current { get; } =
        typeof(PanelVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>
/// Signs and verifies what one panel hands another, in the same envelope shape and with the same
/// algorithm as <see cref="BundleSigning"/>: ECDSA P-256 over SHA-256, IEEE P-1363 fixed-field.
///
/// A copy rather than a reuse of <c>BundleSigning</c> because that one is typed to
/// <see cref="DesiredStateBundle"/> and lives in the contract shared with the agent. Making it
/// generic there would put panel replication in the assembly that ships to every VPN node, which
/// is the one place none of this belongs.
///
/// The signature covers the payload bytes verbatim, exactly as a bundle's does. The payload is
/// deserialized only after the bytes have verified, and never re-serialized to check them: two
/// panels on different builds would otherwise disagree about the fleet because their serializers
/// disagreed about a field order.
/// </summary>
public static class PanelSigning
{
    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    public static SignedPanelPayload Sign<T>(T value, ECDsa signingKey, string keyId, JsonTypeInfo<T> typeInfo)
    {
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
        var signature = signingKey.SignData(Encoding.ASCII.GetBytes(payload), Hash, SignatureFormat);
        return new SignedPanelPayload(payload, Base64Url.Encode(signature), keyId);
    }

    /// <summary>
    /// A stable identifier for the content of a payload, used as the snapshot's ETag.
    ///
    /// Taken over the value with its stamps cleared, not over the signed envelope: the envelope
    /// carries a fresh <c>CreatedAt</c> every time it is built, so hashing it would make every
    /// poll a change and every poll a full transfer. The caller clears the stamps; this only
    /// hashes what it is given.
    /// </summary>
    public static string Fingerprint<T>(T value, JsonTypeInfo<T> typeInfo)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)))
            .ToLowerInvariant()[..32];

    public static bool TryVerify<T>(
        SignedPanelPayload? envelope,
        ECDsa publicKey,
        string expectedKeyId,
        JsonTypeInfo<T> typeInfo,
        out T value,
        out ApiError error)
    {
        value = default!;
        error = ApiError.Empty;

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            error = new ApiError("panel_payload_invalid", "Panel payload is missing its bytes or its signature.");
            return false;
        }

        if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
        {
            error = new ApiError(
                "panel_key_mismatch",
                $"Payload was signed with key '{envelope.KeyId}' but this panel pinned '{expectedKeyId}'. "
                + "It is not from the fleet this panel replicates.");
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
            error = new ApiError("panel_payload_invalid", "Panel payload is not valid base64url.");
            return false;
        }

        if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(envelope.Payload), signature, Hash, SignatureFormat))
        {
            error = new ApiError("panel_signature_invalid", "Panel payload does not verify against the pinned fleet signing key.");
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize(json, typeInfo);
            if (decoded is null)
            {
                error = new ApiError("panel_payload_invalid", "Panel payload decoded to null.");
                return false;
            }

            value = decoded;
            return true;
        }
        catch (JsonException exception)
        {
            error = new ApiError("panel_payload_invalid", $"Panel payload is not valid JSON: {exception.Message}");
            return false;
        }
    }
}

/// <summary>
/// The checks a verified payload still has to pass, and the reason they exist is the same one
/// <see cref="BundleGuard"/> exists for: a correctly signed payload stays correctly signed
/// forever, so a signature alone says nothing about whether this payload is for this panel, now.
///
/// Unlike a bundle there is no origin to distinguish. Nothing a panel replicates is ever read
/// back from its own disk - what is on its disk *is* the applied state - so expiry is enforced
/// unconditionally.
/// </summary>
public static class PanelGuard
{
    /// <param name="appliedAt">The stamp of the newest payload this panel has already applied,
    /// or null before the first one.</param>
    public static ApiError? Check(
        int schemaVersion,
        string boundPanelId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string panelId,
        DateTimeOffset? appliedAt,
        DateTimeOffset now)
    {
        if (!PanelSchema.CanApply(schemaVersion))
        {
            return new ApiError(
                "panel_schema_unsupported",
                $"The active panel speaks replication schema {schemaVersion} and this one understands "
                + $"{PanelSchema.MinimumVersion}-{PanelSchema.CurrentVersion}. Upgrade this panel; "
                + "it keeps serving the state it already has until you do.");
        }

        if (!string.Equals(boundPanelId, panelId, StringComparison.Ordinal))
        {
            return new ApiError(
                "panel_binding_mismatch",
                $"Payload was built for panel '{boundPanelId}', not for '{panelId}'.");
        }

        if (expiresAt <= now)
        {
            return new ApiError(
                "panel_payload_expired",
                $"Payload expired at {expiresAt:O}. A payload this old may be a replay of one captured earlier.");
        }

        // Monotonic, for the one case that matters: a panel that lost a promotion still holds the
        // fleet signing key, so it can still produce payloads that verify. Refusing anything older
        // than what is already applied is what keeps it from winding a standby back to the state
        // the fleet had before it lost.
        if (appliedAt is { } applied && createdAt < applied)
        {
            return new ApiError(
                "panel_payload_stale",
                $"Payload was built at {createdAt:O} and this panel has already applied one from {applied:O}. "
                + "It is from a panel that is behind, not ahead.");
        }

        return null;
    }
}
