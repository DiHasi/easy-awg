namespace AwgEasy.Contracts;

/// <summary>Where a bundle came from, which decides whether its expiry still means anything.</summary>
public enum BundleOrigin
{
    /// <summary>Arrived over the network just now. Expiry is enforced - that is what it is for.</summary>
    ControlPlane,

    /// <summary>Read from this node's own disk: its cached bundle, or one handed to it directly.</summary>
    Local
}

/// <summary>
/// Checks a bundle is acceptable before it is applied. Signature verification alone is not
/// enough: a correctly signed bundle stays valid forever, so an attacker who once captured
/// revision N can replay it later to resurrect a client that has since been revoked.
/// </summary>
public static class BundleGuard
{
    /// <summary>Tolerance for clock skew between control plane and node.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static bool TryAccept(
        DesiredStateBundle? bundle,
        long appliedRevision,
        string expectedNodeId,
        DateTimeOffset now,
        out ApiError error,
        BundleOrigin origin = BundleOrigin.ControlPlane)
    {
        error = ApiError.Empty;

        if (bundle is null)
        {
            error = new ApiError("bundle_missing", "Bundle is missing.");
            return false;
        }

        // A window rather than an exact match: the panel is upgraded before the nodes are, so a
        // fresh agent may be handed the older schema while the fleet is mid-upgrade.
        if (bundle.SchemaVersion < DesiredStateBundle.MinimumSupportedSchemaVersion
            || bundle.SchemaVersion > DesiredStateBundle.CurrentSchemaVersion)
        {
            error = new ApiError(
                "bundle_schema_unsupported",
                $"Bundle schema version {bundle.SchemaVersion} is not supported by this agent (expected {DesiredStateBundle.MinimumSupportedSchemaVersion}-{DesiredStateBundle.CurrentSchemaVersion}).");
            return false;
        }

        // Bound to one node, so a bundle intercepted from one node cannot be replayed at another.
        if (!string.Equals(bundle.NodeId, expectedNodeId, StringComparison.Ordinal))
        {
            error = new ApiError("bundle_node_mismatch", "Bundle was issued for a different node.");
            return false;
        }

        // Rollback protection: never move backwards. Re-applying the same revision is allowed
        // so the agent can self-heal after a failed apply.
        if (bundle.Revision < appliedRevision)
        {
            error = new ApiError(
                "bundle_stale_revision",
                $"Bundle revision {bundle.Revision} is older than the applied revision {appliedRevision}.");
            return false;
        }

        if (bundle.IssuedAt - ClockSkew > now)
        {
            error = new ApiError("bundle_not_yet_valid", "Bundle is issued in the future.");
            return false;
        }

        // Expiry is a freshness rule for the wire, not a property of a file on disk. A node re-applies
        // its cached bundle at boot to bring the tunnel up before it has spoken to anyone, and an
        // unreachable - or permanently gone - control plane must never be the reason it stops carrying
        // traffic. Getting a stale bundle into that cache means root on the node, which already beats
        // every check here, and the revision check above still refuses a rollback.
        if (origin == BundleOrigin.ControlPlane && bundle.ExpiresAt + ClockSkew < now)
        {
            error = new ApiError("bundle_expired", "Bundle has expired.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(bundle.Identity.ServerPrivateKey) || string.IsNullOrWhiteSpace(bundle.Identity.ServerPublicKey))
        {
            error = new ApiError("bundle_identity_missing", "Bundle does not contain the fleet identity.");
            return false;
        }

        if (!Ipv4Network.TryParse(bundle.Network.Subnet, out var network, out error))
        {
            return false;
        }

        if (bundle.Network.ListenPort is < 1 or > 65535)
        {
            error = new ApiError("bundle_invalid_port", "Listen port must be between 1 and 65535.");
            return false;
        }

        if (!AwgObfuscationValidator.TryValidateServerProfile(bundle.Obfuscation, out error))
        {
            return false;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenAddresses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var peer in bundle.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.PublicKey) || string.IsNullOrWhiteSpace(peer.Address))
            {
                error = new ApiError("bundle_invalid_peer", "Bundle contains a peer with missing fields.");
                return false;
            }

            if (!seenKeys.Add(peer.PublicKey))
            {
                error = new ApiError("bundle_duplicate_peer", $"Bundle contains duplicate peer key {peer.PublicKey}.");
                return false;
            }

            if (!System.Net.IPAddress.TryParse(peer.Address, out var address) || !network.Contains(address))
            {
                error = new ApiError("bundle_peer_outside_subnet", $"Peer address {peer.Address} is not inside {bundle.Network.Subnet}.");
                return false;
            }

            if (!seenAddresses.Add(peer.Address))
            {
                error = new ApiError("bundle_duplicate_address", $"Bundle contains duplicate peer address {peer.Address}.");
                return false;
            }
        }

        return true;
    }
}
