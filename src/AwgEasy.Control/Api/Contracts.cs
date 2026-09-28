using AwgEasy.Contracts;

namespace AwgEasy.Control;

// What crosses the wire. Deliberately separate from the domain records above: an API type is a
// promise to a caller, and the compiler should complain if a secret-bearing record is ever
// returned where one of these is expected.
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

/// <param name="PublicIp">Where this node says it is reachable, as discovered by the agent itself.</param>
/// <param name="IsActive">True for the one node the failover DNS record currently points at.</param>
public sealed record NodeResponse(
    string Id,
    string Name,
    string? Hostname,
    string? EndpointHost,
    string? PublicIp,
    bool IsActive,
    string Status,
    long AppliedRevision,
    long FleetRevision,
    bool InSync,
    bool InterfaceUp,
    string? Backend,
    string? AgentVersion,
    int BundleSchemaVersion,
    bool SupportsCurrentSchema,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    bool Revoked,
    DateTimeOffset EnrolledAt)
{
    public static NodeResponse From(NodeRecord node, long fleetRevision, string? activeNodeId)
        => new(
            node.Id, node.Name, node.Hostname, node.EndpointHost,
            node.PublicIp, string.Equals(node.Id, activeNodeId, StringComparison.Ordinal),
            node.Status,
            node.AppliedRevision, fleetRevision, node.AppliedRevision == fleetRevision,
            node.InterfaceUp, node.Backend, node.AgentVersion,
            node.BundleSchemaVersion,
            node.BundleSchemaVersion >= DesiredStateBundle.CurrentSchemaVersion,
            node.LastSeenAt, node.LastError, node.Revoked, node.EnrolledAt);
}

/// <summary>
/// The state of manual failover: which node the record points at, and whether the world agrees.
/// </summary>
/// <param name="ProviderConfigured">False means the record is the operator&apos;s to edit by hand.</param>
/// <param name="ResolvedAddresses">What the panel&apos;s own resolver currently answers with.</param>
/// <param name="Matches">Whether that answer is the active node&apos;s address.</param>
public sealed record DnsStatusResponse(
    string Provider,
    bool ProviderConfigured,
    string RecordName,
    string RecordType,
    int Ttl,
    string? ActiveNodeId,
    string? ActiveNodeName,
    string? TargetAddress,
    string[] ResolvedAddresses,
    bool Matches,
    DateTimeOffset? ActivatedAt,
    string? Warning);

public sealed record CreateNodeRequest(string Name, string? EndpointHost, string? EgressInterface, int? Mtu);

/// <summary>The plaintext token is returned exactly once; only its hash is stored.</summary>
public sealed record EnrollmentTokenResponse(string Token, string NodeName, DateTimeOffset ExpiresAt, string InstallCommand);

public sealed record LoginRequest(string Username, string Password);

public sealed record EventResponse(long Id, DateTimeOffset At, string Kind, string? Actor, string? NodeId, string Message);

/// <param name="ReceivedBytes">Bytes this peer sent into the tunnel, net of any counter reset.</param>
/// <param name="StatsResetAt">When the counters were last zeroed here, or null if they are lifetime totals.</param>
public sealed record ClientStatsResponse(
    string Id,
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes,
    bool Online,
    string? NodeId,
    DateTimeOffset? StatsResetAt);

public sealed record ImportResultResponse(int ClientsImported, long Revision, string[] Warnings);

public sealed record ClientShareResponse(string Token, string ClientName, DateTimeOffset ExpiresAt, string Url);

/// <summary>
/// What an anonymous visitor holding a share link is allowed to see: no keys, no address, and not
/// the client name either - that label is the operator&apos;s own bookkeeping, and the person on the
/// other end of the link has no business reading what they were filed under.
/// </summary>
public sealed record PublicShareResponse(DateTimeOffset ExpiresAt);

public sealed record HealthResponse(string Status);

/// <summary>
/// A freshly generated AmneziaWG 3.x header protection key. Returned for the operator to paste
/// into the profile rather than stored on the side: it only takes effect once the profile is
/// saved, and a key nobody saved should leave no trace.
/// </summary>
public sealed record GeneratedKeyResponse(string Key);
