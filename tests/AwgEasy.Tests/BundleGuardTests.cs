using AwgEasy.Contracts;

namespace AwgEasy.Tests;

public class BundleGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DesiredStateBundle Bundle(
        long revision = 10,
        string nodeId = "node-a",
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        BundlePeer[]? peers = null,
        int schemaVersion = DesiredStateBundle.CurrentSchemaVersion,
        string subnet = "10.8.0.0/24")
        => new(
            schemaVersion,
            revision,
            nodeId,
            issuedAt ?? Now.AddMinutes(-1),
            expiresAt ?? Now.AddMinutes(10),
            new FleetIdentity(1, "private", "public"),
            new NetworkProfile(subnet, 51820),
            null,
            new NodeSettings("awg0", null, null),
            peers ?? [new BundlePeer("key-1", "psk-1", "10.8.0.2")]);

    [Fact]
    public void Accepts_a_well_formed_bundle()
    {
        Assert.True(BundleGuard.TryAccept(Bundle(), appliedRevision: 5, "node-a", Now, out var error), error.Message);
    }

    // Rollback protection: a correctly signed bundle stays valid forever, so an attacker who
    // captured revision N could otherwise replay it to resurrect a revoked client.
    [Fact]
    public void Rejects_a_revision_older_than_the_one_already_applied()
    {
        Assert.False(BundleGuard.TryAccept(Bundle(revision: 9), appliedRevision: 10, "node-a", Now, out var error));
        Assert.Equal("bundle_stale_revision", error.Code);
    }

    [Fact]
    public void Allows_reapplying_the_current_revision_so_a_failed_apply_can_self_heal()
    {
        Assert.True(BundleGuard.TryAccept(Bundle(revision: 10), appliedRevision: 10, "node-a", Now, out _));
    }

    [Fact]
    public void Rejects_a_bundle_issued_for_another_node()
    {
        Assert.False(BundleGuard.TryAccept(Bundle(nodeId: "node-b"), appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_node_mismatch", error.Code);
    }

    [Fact]
    public void Rejects_an_expired_bundle()
    {
        var expired = Bundle(issuedAt: Now.AddHours(-2), expiresAt: Now.AddHours(-1));
        Assert.False(BundleGuard.TryAccept(expired, appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_expired", error.Code);
    }

    [Fact]
    public void Tolerates_clock_skew_within_the_allowed_window()
    {
        // Node clock slightly behind the control plane must not reject a fresh bundle.
        var justIssued = Bundle(issuedAt: Now.AddMinutes(2), expiresAt: Now.AddMinutes(12));
        Assert.True(BundleGuard.TryAccept(justIssued, appliedRevision: 0, "node-a", Now, out var error), error.Message);
    }

    [Fact]
    public void Rejects_a_bundle_issued_far_in_the_future()
    {
        var future = Bundle(issuedAt: Now.AddHours(1), expiresAt: Now.AddHours(2));
        Assert.False(BundleGuard.TryAccept(future, appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_not_yet_valid", error.Code);
    }

    [Fact]
    public void Rejects_an_unknown_schema_version()
    {
        Assert.False(BundleGuard.TryAccept(Bundle(schemaVersion: 99), appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_schema_unsupported", error.Code);
    }

    [Fact]
    public void Rejects_a_peer_outside_the_tunnel_subnet()
    {
        var peers = new[] { new BundlePeer("key-1", "psk-1", "192.168.5.9") };
        Assert.False(BundleGuard.TryAccept(Bundle(peers: peers), appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_peer_outside_subnet", error.Code);
    }

    [Fact]
    public void Rejects_duplicate_peer_addresses()
    {
        var peers = new[]
        {
            new BundlePeer("key-1", "psk-1", "10.8.0.2"),
            new BundlePeer("key-2", "psk-2", "10.8.0.2")
        };

        Assert.False(BundleGuard.TryAccept(Bundle(peers: peers), appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_duplicate_address", error.Code);
    }

    [Fact]
    public void Rejects_duplicate_peer_keys()
    {
        var peers = new[]
        {
            new BundlePeer("key-1", "psk-1", "10.8.0.2"),
            new BundlePeer("key-1", "psk-2", "10.8.0.3")
        };

        Assert.False(BundleGuard.TryAccept(Bundle(peers: peers), appliedRevision: 0, "node-a", Now, out var error));
        Assert.Equal("bundle_duplicate_peer", error.Code);
    }
}
