using AwgEasy.Contracts;

namespace AwgEasy.Control;

// What crosses the wire. Deliberately separate from the domain records above: an API type is a
// promise to a caller, and the compiler should complain if a secret-bearing record is ever
// returned where one of these is expected.
/// <param name="GroupId">The person this config was filed under, or null for the ungrouped list.</param>
public sealed record ClientResponse(
    string Id,
    string Name,
    string Address,
    string PublicKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ClientObfuscationOverrides? Obfuscation,
    string? GroupId)
{
    public static ClientResponse From(ClientRecord client)
        => new(client.Id, client.Name, client.Address, client.PublicKey, client.Enabled, client.CreatedAt, client.UpdatedAt, client.Obfuscation, client.GroupId);
}

/// <param name="GroupId">Files the new config under a person right away, so a device added from
/// that person's own section does not have to be dragged there afterwards.</param>
public sealed record CreateClientRequest(string Name, ClientObfuscationOverrides? Obfuscation, string? GroupId = null);

public sealed record UpdateClientRequest(string Name);

/// <summary>
/// A person, holding however many devices. The list order is the arrangement itself: both the
/// group list and the peers inside a group are returned in the order they should be read in.
/// </summary>
public sealed record ClientGroupResponse(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ClientGroupResponse From(ClientGroupRecord group)
        => new(group.Id, group.Name, group.CreatedAt, group.UpdatedAt);
}

public sealed record CreateClientGroupRequest(string Name);

public sealed record UpdateClientGroupRequest(string Name);

public sealed record ReorderClientGroupsRequest(string[] Ids);

/// <param name="GroupId">Null for the bucket of peers filed nowhere.</param>
public sealed record ClientGroupPlacement(string? GroupId, string[] ClientIds);

/// <summary>
/// Where the peers should sit after a drag. Only the buckets listed are rewritten, so a panel
/// open in another browser cannot have a peer it has never heard of swept into the first group.
/// </summary>
public sealed record ArrangeClientsRequest(ClientGroupPlacement[] Groups);

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
/// <param name="Health">One of <see cref="NodeHealthStates"/>: whether clients can reach it, not just whether it runs.</param>
/// <param name="HealthSource">"probes" when probe results decided <paramref name="Health"/>, "agent" when only the node's own reports did.</param>
/// <param name="FailoverPriority">Lower is tried first when automatic failover picks a node.</param>
/// <param name="AutoFailover">False keeps automatic failover from ever sending traffic here.</param>
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
    DateTimeOffset EnrolledAt,
    string Health,
    string HealthReason,
    string HealthSource,
    DateTimeOffset? LastReachableAt,
    DateTimeOffset? FailingSince,
    int ProbesReachable,
    int ProbesReporting,
    int FailoverPriority,
    bool AutoFailover)
{
    public static NodeResponse From(NodeRecord node, long fleetRevision, string? activeNodeId, NodeAssessment health)
        => new(
            node.Id, node.Name, node.Hostname, node.EndpointHost,
            node.PublicIp, string.Equals(node.Id, activeNodeId, StringComparison.Ordinal),
            node.Status,
            node.AppliedRevision, fleetRevision, node.AppliedRevision == fleetRevision,
            node.InterfaceUp, node.Backend, node.AgentVersion,
            node.BundleSchemaVersion,
            node.BundleSchemaVersion >= DesiredStateBundle.CurrentSchemaVersion,
            node.LastSeenAt, node.LastError, node.Revoked, node.EnrolledAt,
            health.State, health.Reason, health.Source, health.LastGoodAt, health.FailingSince,
            health.ProbesReachable, health.ProbesReporting,
            node.FailoverPriority, node.AutoFailover);
}

public sealed record UpdateNodeFailoverRequest(int Priority, bool AutoFailover);

/// <param name="Mode">One of <see cref="FailoverModes"/>.</param>
/// <param name="NotificationChannels">Where notifications go; empty when none is configured.</param>
/// <param name="EvaluatedAt">When the monitor last looked at the fleet. Null until its first round.</param>
/// <param name="Action">What that round decided: none, wait, hold, stuck, switch, recommend or failed.</param>
public sealed record FailoverStatusResponse(
    string Mode,
    string Provider,
    bool ProviderConfigured,
    int CheckIntervalSeconds,
    int GraceSeconds,
    int CooldownSeconds,
    int NodeStaleSeconds,
    int ProbeStaleSeconds,
    string[] NotificationChannels,
    DateTimeOffset? EvaluatedAt,
    string? Action,
    string? Message,
    string? ActiveNodeId,
    string? TargetNodeId);

public sealed record UpdateFailoverRequest(string Mode);

public sealed record CreateProbeRequest(string Name);

public sealed record ProbeResultResponse(
    string NodeId,
    string Address,
    string Outcome,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastReachableAt,
    int? LatencyMs,
    string? Detail,
    bool? Handshake);

public sealed record ProbeResponse(
    string Id,
    string Name,
    string? Hostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    bool Revoked,
    DateTimeOffset EnrolledAt,
    ProbeResultResponse[] Results);

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
