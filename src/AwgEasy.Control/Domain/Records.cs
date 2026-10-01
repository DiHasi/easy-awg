using AwgEasy.Contracts;

namespace AwgEasy.Control;

// What the control plane stores. These mirror the database rows and carry secrets - client
// private keys, the fleet identity - so they must never be returned from an endpoint directly.

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
    string EndpointHost,
    ServerObfuscationProfile? Obfuscation,
    long Revision);

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
    DateTimeOffset UpdatedAt);

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
    bool AutoFailover = true)
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
