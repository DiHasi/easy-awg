using AwgEasy.Contracts;

namespace AwgEasy.Control;

// What the control plane stores. These mirror the database rows and carry secrets - client
// private keys, the fleet identity - so they must never be returned from an endpoint directly.

/// <param name="TunnelMtu">The interface MTU every client, every probe and every node without an
/// override runs. One value for all three because each must be sized against the same worst-case
/// client path; see <see cref="FleetService.DefaultTunnelMtu"/> for why the default is what it
/// is.</param>
/// <param name="IdentityRevision">
/// The revision at which <see cref="ServerPrivateKey"/> and the fleet's preshared keys last
/// changed, or 0 on a fleet that has never been rotated. A node below it is still running the
/// previous identity, so it serves no config this panel has issued since - which is a different
/// thing from merely being out of sync, and the one state traffic must never be sent into.
/// </param>
/// <param name="IdentityRotatedAt">When that happened. Null until the first rotation.</param>
public sealed record FleetRecord(
    int Generation,
    string ServerPrivateKey,
    string ServerPublicKey,
    string SigningPrivateKey,
    string SigningPublicKey,
    string SigningKeyId,
    string Subnet,
    int ListenPort,
    string ClientAllowedIps,
    string? ClientDns,
    int TunnelMtu,
    string EndpointHost,
    ServerObfuscationProfile? Obfuscation,
    long Revision,
    long IdentityRevision = 0,
    DateTimeOffset? IdentityRotatedAt = null);

/// <param name="GroupId">The person this config belongs to, or null while it is filed nowhere.
/// The panel's own bookkeeping: no node is told about it and no config changes with it.</param>
/// <param name="ConfigGeneration">The fleet generation this peer's config was last rendered at,
/// or 0 while the panel has never rendered one. Below <see cref="FleetRecord.Generation"/> it
/// means the config this person is holding was issued against an identity the fleet has since
/// replaced, and no longer works anywhere.</param>
/// <param name="EgressLabel">Which of a node's addresses this peer leaves through, by the
/// operator's name for it, or null to leave through that node's default. A label rather than an
/// address, so it still means something after failover has moved traffic to another node.</param>
public sealed record ClientRecord(
    string Id,
    string Name,
    string Address,
    string PrivateKey,
    string PublicKey,
    string PresharedKey,
    bool Enabled,
    ClientObfuscationOverrides? Obfuscation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? GroupId = null,
    int ConfigGeneration = 0,
    string? EgressLabel = null);

/// <summary>One person, holding however many devices. Nothing a node ever sees.</summary>
public sealed record ClientGroupRecord(
    string Id,
    string Name,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One bucket of the peer list as the operator arranged it: the group it belongs to - null for
/// the peers filed nowhere - and its peers in the order they should be read in.
/// </summary>
public sealed record ClientPlacement(string? GroupId, IReadOnlyList<string> ClientIds);

/// <param name="EgressPlan">
/// The operator's egress plan for this node as the column holds it - which address is the
/// default, and what the others are called. Carried as the JSON it is stored as, like the
/// obfuscation profile: a node's own settings must not be able to drift because two panels
/// serialized them differently.
/// </param>
/// <param name="EgressReport">
/// What the agent last reported about the addresses its egress interface carries, as JSON. The
/// node's own observation rather than anybody's choice, which is why it is kept apart from the
/// plan and replicated with the rest of the evidence.
/// </param>
public sealed record NodeRecord(
    string Id,
    string Name,
    string? Hostname,
    string? EndpointHost,
    string AgentPublicKey,
    string? AgentVersion,
    string Status,
    long AppliedRevision,
    bool InterfaceUp,
    string? Backend,
    int BundleSchemaVersion,
    string? EgressInterface,
    int? Mtu,
    string? PublicIp,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    bool Revoked,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastUpAt = null,
    int FailoverPriority = NodeRecord.DefaultFailoverPriority,
    bool AutoFailover = true,
    string? EgressPlan = null,
    string? EgressReport = null)
{
    /// <summary>Lower goes first. Left at this, nodes are tried in the order they enrolled.</summary>
    public const int DefaultFailoverPriority = 100;
}

/// <param name="ClientId">The client row whose key the probe tunnels with. Hidden from the client
/// list, because it is not a person's, but a peer on every node like any other.</param>
public sealed record ProbeRecord(
    string Id,
    string Name,
    string? Hostname,
    string AgentPublicKey,
    string? AgentVersion,
    string ClientId,
    DateTimeOffset? LastSeenAt,
    bool Revoked,
    DateTimeOffset EnrolledAt);

/// <summary>The latest thing one probe found out about one node.</summary>
public sealed record ProbeObservation(
    string ProbeId,
    string NodeId,
    string Address,
    string Outcome,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastReachableAt,
    DateTimeOffset? FailingSince,
    int? LatencyMs,
    string? Detail,
    bool? Handshake = null);
