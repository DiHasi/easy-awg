using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// The single gate every bundle passes through before it can touch the interface:
/// verify the signature against the pinned control-plane key, then apply the shared
/// <see cref="BundleGuard"/> rules (schema, node binding, revision monotonicity, freshness).
/// </summary>
public sealed class BundleAcceptor(ILogger<BundleAcceptor> logger)
{
    public bool TryAccept(
        SignedBundle? envelope,
        AgentIdentityDocument identity,
        DateTimeOffset now,
        out DesiredStateBundle bundle,
        out ApiError error)
    {
        bundle = default!;

        if (string.IsNullOrWhiteSpace(identity.ControlSigningPublicKey) || string.IsNullOrWhiteSpace(identity.ControlSigningKeyId))
        {
            error = new ApiError("control_key_not_pinned", "No control plane signing key is pinned; the node is not enrolled.");
            return false;
        }

        using var controlKey = BundleSigning.ImportPublicKey(identity.ControlSigningPublicKey);
        if (!BundleSigning.TryVerify(envelope, controlKey, identity.ControlSigningKeyId, ContractsJsonContext.Default.DesiredStateBundle, out var decoded, out error))
        {
            logger.LogError("Rejected bundle: {Code} - {Message}", error.Code, error.Message);
            return false;
        }

        if (!BundleGuard.TryAccept(decoded, identity.AppliedRevision, identity.NodeId ?? string.Empty, now, out error))
        {
            logger.LogError("Rejected bundle revision {Revision}: {Code} - {Message}", decoded.Revision, error.Code, error.Message);
            return false;
        }

        bundle = decoded;
        return true;
    }
}
