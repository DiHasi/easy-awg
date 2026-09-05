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
    string? EgressInterface,
    int? Mtu,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    bool Revoked,
    DateTimeOffset EnrolledAt);
