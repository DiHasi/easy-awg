namespace AwgEasy.Contracts;

/// <summary>
/// Transport envelope for <see cref="DesiredStateBundle"/>.
///
/// The signature covers <see cref="Payload"/> verbatim, so the agent must verify the bytes it
/// received rather than re-serializing the deserialized bundle: any canonicalization difference
/// between control plane and agent would otherwise break verification.
/// </summary>
/// <param name="Payload">Base64url of the UTF-8 JSON of the bundle.</param>
/// <param name="Signature">Base64url ECDSA P-256/SHA-256 signature (IEEE P-1363 fixed-field) over the raw bytes of <paramref name="Payload"/>.</param>
/// <param name="KeyId">Identifies which control-plane signing key was used, so keys can be rotated.</param>
public sealed record SignedBundle(
    string Payload,
    string Signature,
    string KeyId);
