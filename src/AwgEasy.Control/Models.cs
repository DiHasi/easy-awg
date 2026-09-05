using AwgEasy.Contracts;

namespace AwgEasy.Control;

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

// ---- API surface ----

public sealed record ClientResponse(
    string Id,
    string Name,
    string Address,
    string PublicKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ClientObfuscationOverrides? Obfuscation)
{
    public static ClientResponse From(ClientRecord client)
        => new(client.Id, client.Name, client.Address, client.PublicKey, client.Enabled, client.CreatedAt, client.UpdatedAt, client.Obfuscation);
}

public sealed record CreateClientRequest(string Name, ClientObfuscationOverrides? Obfuscation);

public sealed record UpdateClientRequest(string Name);

public sealed record FleetResponse(
    int Generation,
    string ServerPublicKey,
    string Subnet,
    int ListenPort,
    string ClientAllowedIps,
    string? ClientDns,
    string EndpointHost,
    ServerObfuscationProfile? Obfuscation,
    long Revision,
    int ClientsCount,
    int NodesCount);

public sealed record NodeResponse(
    string Id,
    string Name,
    string? Hostname,
    string? EndpointHost,
    string Status,
    long AppliedRevision,
    long FleetRevision,
    bool InSync,
    bool InterfaceUp,
    string? Backend,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    bool Revoked,
    DateTimeOffset EnrolledAt)
{
    public static NodeResponse From(NodeRecord node, long fleetRevision)
        => new(
            node.Id, node.Name, node.Hostname, node.EndpointHost, node.Status,
            node.AppliedRevision, fleetRevision, node.AppliedRevision == fleetRevision,
            node.InterfaceUp, node.Backend, node.AgentVersion, node.LastSeenAt,
            node.LastError, node.Revoked, node.EnrolledAt);
}

public sealed record CreateNodeRequest(string Name, string? EndpointHost, string? EgressInterface, int? Mtu);

/// <summary>The plaintext token is returned exactly once; only its hash is stored.</summary>
public sealed record EnrollmentTokenResponse(string Token, string NodeName, DateTimeOffset ExpiresAt, string InstallCommand);

public sealed record LoginRequest(string Username, string Password);

public sealed record EventResponse(long Id, DateTimeOffset At, string Kind, string? Actor, string? NodeId, string Message);

public sealed record ClientStatsResponse(
    string Id,
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes,
    bool Online,
    string? NodeId);

public sealed record ImportResultResponse(int ClientsImported, long Revision, string[] Warnings);

public sealed record ClientShareResponse(string Token, string ClientName, DateTimeOffset ExpiresAt, string Url);

/// <summary>What an anonymous visitor holding a share link is allowed to see: no keys, no address.</summary>
public sealed record PublicShareResponse(string ClientName, DateTimeOffset ExpiresAt);

public sealed record HealthResponse(string Status);
