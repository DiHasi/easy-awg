using AwgEasy.Contracts;

namespace AwgEasy.Control;

// What crosses the wire. Deliberately separate from the domain records above: an API type is a
// promise to a caller, and the compiler should complain if a secret-bearing record is ever
// returned where one of these is expected.
/// <param name="GroupId">The person this config was filed under, or null for the ungrouped list.</param>
/// <param name="ConfigGeneration">Which fleet generation this peer's config was last rendered at,
/// or 0 while the panel has never rendered one.</param>
/// <param name="ConfigStale">
/// True when a config was handed out for this peer and the identity it was issued against has
/// since been replaced, so it works on no node and has to be handed out again. Carried beside the
/// two numbers rather than left to the caller for the same reason <see cref="NodeResponse.InSync"/>
/// is: the comparison is the panel's to make, not every page's.
///
/// A peer the panel has never rendered a config for is not stale. Nothing of its is in anybody's
/// hands, and its own key material is current - saying its config is broken would be a claim
/// about a config that was never issued.
/// </param>
public sealed record ClientResponse(
    string Id,
    string Name,
    string Address,
    string PublicKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ClientObfuscationOverrides? Obfuscation,
    string? GroupId,
    int ConfigGeneration,
    int FleetGeneration,
    bool ConfigStale,
    string? EgressLabel)
{
    public static ClientResponse From(ClientRecord client, int fleetGeneration)
        => new(
            client.Id, client.Name, client.Address, client.PublicKey, client.Enabled,
            client.CreatedAt, client.UpdatedAt, client.Obfuscation, client.GroupId,
            client.ConfigGeneration,
            fleetGeneration,
            client.ConfigGeneration != 0 && client.ConfigGeneration != fleetGeneration,
            client.EgressLabel);
}

/// <param name="GroupId">Files the new config under a person right away, so a device added from
/// that person's own section does not have to be dragged there afterwards.</param>
/// <param name="EgressLabel">Which of a node's addresses this peer leaves through, so a config
/// handed out for a particular reason is born with it rather than being corrected afterwards.</param>
public sealed record CreateClientRequest(
    string Name,
    ClientObfuscationOverrides? Obfuscation,
    string? GroupId = null,
    string? EgressLabel = null);

public sealed record UpdateClientRequest(string Name);

/// <summary>
/// Which address this peer leaves through, by the operator's name for it. Null unpins it.
///
/// Its own request rather than a field on <see cref="UpdateClientRequest"/>, which renames and
/// nothing else: folding it in would mean the rename dialog, which sends a name alone, unpinned
/// whoever it renamed.
/// </summary>
public sealed record UpdateClientEgressRequest(string? Label);

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

/// <param name="StaleConfigsCount">Peers still holding a config from an older generation. Zero
/// on a fleet nobody has rotated, and the number of people who have to be found afterwards on one
/// somebody just did.</param>
/// <param name="IdentityRevision">The revision the identity last changed at, or 0 on a fleet that
/// has never been rotated. A node below it is on the previous identity and serves nothing.</param>
/// <param name="IdentityRotatedAt">When that happened. Null until the first rotation.</param>
public sealed record FleetResponse(
    int Generation,
    string ServerPublicKey,
    string Subnet,
    int ListenPort,
    string ClientAllowedIps,
    string? ClientDns,
    int TunnelMtu,
    string EndpointHost,
    ServerObfuscationProfile? Obfuscation,
    long Revision,
    int ClientsCount,
    int NodesCount,
    int StaleConfigsCount,
    long IdentityRevision,
    DateTimeOffset? IdentityRotatedAt);

/// <param name="ReissueClientKeys">
/// Also replace every peer's own key pair. Off by default: a node never held a client private key,
/// so a node read off its disk does not expose one. On when the panel's own database has to be
/// assumed read too.
/// </param>
public sealed record RotateFleetRequest(bool ReissueClientKeys = false);

/// <param name="KeyPairReissued">Whether this peer's own key pair changed as well as its preshared
/// key. The same for every peer in one rotation; carried per peer so a reader does not have to
/// hold the request to know what it is looking at.</param>
public sealed record RotatedClientResponse(string Id, string Name, string PublicKey, bool KeyPairReissued);

/// <summary>
/// What a rotation did. Every config in the field stopped working the moment this returned, so the
/// peer list is part of the answer rather than something to go and look up: it is the list of
/// people who now have to be handed a new config.
/// </summary>
/// <param name="ProbesReissued">Probe peers re-keyed alongside them. They need no hand-holding -
/// a probe renders its config from the panel on its next poll - but they were re-keyed too.</param>
/// <param name="ShareLinksRevoked">Share links that stopped resolving, because each named the
/// generation that has just been replaced.</param>
/// <param name="Warning">
/// What a rotation does not do, in the panel's own words - the one thing an operator who revoked
/// a node and then rotated has every reason to believe is handled and is not. Carried on the
/// response rather than written into the page like <see cref="DnsStatusResponse.Warning"/>, so a
/// caller driving the API hears it too.
/// </param>
public sealed record FleetRotationResponse(
    int Generation,
    int PreviousGeneration,
    string ServerPublicKey,
    long Revision,
    bool ClientKeysReissued,
    int ShareLinksRevoked,
    int ProbesReissued,
    RotatedClientResponse[] Clients,
    string Warning);

/// <summary>
/// The interface MTU every client, probe and node without an override runs. One value for all
/// three: each is sized against the same worst-case client path, and two knobs that must agree
/// with nothing enforcing it is how a fleet ends up half working.
/// </summary>
public sealed record UpdateTunnelMtuRequest(int Mtu);

/// <param name="PublicIp">Where this node says it is reachable, as discovered by the agent itself.</param>
/// <param name="IsActive">True for the one node the failover DNS record currently points at.</param>
/// <param name="Health">One of <see cref="NodeHealthStates"/>: whether clients can reach it, not just whether it runs.</param>
/// <param name="HealthSource">"probes" when probe results decided <paramref name="Health"/>, "agent" when only the node's own reports did.</param>
/// <param name="FailoverPriority">Lower is tried first when automatic failover picks a node.</param>
/// <param name="AutoFailover">False keeps automatic failover from ever sending traffic here.</param>
/// <param name="EgressInterface">What the node NATs through, or null when its agent detects it.</param>
/// <param name="Mtu">This node's own tunnel MTU, or null when it runs the fleet value.</param>
/// <param name="OnCurrentIdentity">
/// False means this node has not applied the last rotation: it is still answering with the
/// previous key pair and preshared keys, so no config issued since can reach it. Distinct from
/// <paramref name="InSync"/> on purpose - being one peer behind is an inconvenience, being an
/// identity behind means the node serves nobody, and traffic may not be sent there at all.
/// </param>
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
    bool AutoFailover,
    string? EgressInterface,
    int? Mtu,
    bool OnCurrentIdentity,
    NodeEgressPlan EgressPlan,
    ReportedEgressAddress[] EgressAddresses,
    int PeersOnline,
    int PeersIdle)
{
    public static NodeResponse From(
        NodeRecord node,
        long fleetRevision,
        long identityRevision,
        string? activeNodeId,
        NodeAssessment health,
        NodePeerLoad peers)
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
            node.FailoverPriority, node.AutoFailover,
            node.EgressInterface, node.Mtu,
            node.AppliedRevision >= identityRevision,
            NodeEgressPlan.Read(node.EgressPlan),
            ReportedEgress.Read(node.EgressReport),
            peers.Online,
            peers.Idle);
}

/// <summary>Who one node is carrying, and who would notice if it went away.</summary>
/// <param name="EvidenceAt">
/// That node's last status report. The whole answer is as old as this: a node nothing has heard
/// from reads zero online because nothing vouches for anybody, not because it is empty.
/// </param>
public sealed record NodePeersResponse(
    string NodeId,
    int Online,
    int Idle,
    DateTimeOffset? EvidenceAt,
    NodePeerResponse[] Peers);

/// <param name="Online">
/// Handshaking through this node right now, by the same window the peer list calls a peer online.
/// False is a peer whose last known node is this one and which is saying nothing - the one a
/// shutdown breaks quietly.
/// </param>
public sealed record NodePeerResponse(
    string Id,
    string Name,
    string Address,
    DateTimeOffset? LatestHandshakeAt,
    bool Online);

public sealed record UpdateNodeFailoverRequest(int Priority, bool AutoFailover);

/// <summary>
/// Which source addresses this node hands out and under what names.
///
/// Its own request rather than part of <see cref="UpdateNodeRequest"/>, and deliberately not part
/// of <see cref="CreateNodeRequest"/> either: at enrollment nobody knows which addresses the node
/// carries - they arrive with its first status report - so this is not a setting a one-time token
/// could carry.
/// </summary>
public sealed record UpdateNodeEgressRequest(string? DefaultAddress, EgressLabel[]? Labels);

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

/// <summary>
/// What a node is to be enrolled as. The three optional settings are carried on the token and
/// written to the node row when the agent enrolls, so a node that needs a narrower MTU or a named
/// egress interface has them in the very first bundle it fetches.
/// </summary>
public sealed record CreateNodeRequest(string Name, string? EndpointHost, string? EgressInterface, int? Mtu);

/// <summary>
/// The same settings on a node that already exists. Changing the MTU or the egress interface
/// changes what that node runs and moves the fleet revision; changing the name or the endpoint
/// host does not, like a client rename.
/// </summary>
public sealed record UpdateNodeRequest(string Name, string? EndpointHost, string? EgressInterface, int? Mtu);

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

/// <summary>One bucket of a traffic series. <paramref name="At"/> is where the bucket starts, UTC.</summary>
public sealed record UsagePoint(DateTimeOffset At, long ReceivedBytes, long TransmittedBytes);

/// <param name="ActiveDays">Days in the window on which this peer moved anything at all.</param>
/// <param name="LastActiveAt">When traffic was last credited to this peer inside the window.</param>
public sealed record ClientUsageResponse(
    string Id,
    string Name,
    string? GroupId,
    bool Enabled,
    long ReceivedBytes,
    long TransmittedBytes,
    int ActiveDays,
    DateTimeOffset? LastActiveAt);

/// <summary>
/// What the fleet moved over a window, as a series and as a number per peer. The series is dense -
/// a bucket nothing happened in is a zero rather than a gap - so a chart drawn from it shows the
/// quiet hours instead of closing over them.
/// </summary>
/// <param name="Bucket">"hour" or "day": how wide one point of <paramref name="Series"/> is.</param>
/// <param name="RecordingSince">
/// The oldest hour still kept. A window that reaches back further than this is mostly a statement
/// about retention, and the panel says so rather than drawing an empty month.
/// </param>
/// <param name="RetentionDays">How long history is kept. Zero means nothing is being recorded.</param>
public sealed record UsageSummaryResponse(
    string Window,
    string Bucket,
    DateTimeOffset From,
    DateTimeOffset To,
    long ReceivedBytes,
    long TransmittedBytes,
    UsagePoint[] Series,
    ClientUsageResponse[] Clients,
    int RetentionDays,
    DateTimeOffset? RecordingSince);

/// <summary>One peer's own history over the same windows.</summary>
public sealed record ClientUsageSeriesResponse(
    string ClientId,
    string Window,
    string Bucket,
    DateTimeOffset From,
    DateTimeOffset To,
    long ReceivedBytes,
    long TransmittedBytes,
    int ActiveDays,
    UsagePoint[] Series,
    int RetentionDays,
    DateTimeOffset? RecordingSince);

/// <summary>
/// One bucket of a node availability series. The counters are samples - one per failover round -
/// so <paramref name="Samples"/> is the denominator of any share taken from the rest, and zero of
/// them means the panel recorded nothing in that bucket rather than that the node was never up.
/// </summary>
public sealed record NodeAvailabilityPoint(
    DateTimeOffset At,
    int Samples,
    int Healthy,
    int Blocked,
    int Down,
    int Silent,
    int Unknown);

/// <param name="Uptime">
/// The share of samples in which the node was healthy, 0 to 1, or null when nothing was recorded.
/// Null rather than zero: "no history" and "never once healthy" are opposite answers.
/// </param>
public sealed record NodeAvailabilityResponse(
    string NodeId,
    string Name,
    bool Revoked,
    int Samples,
    int Healthy,
    int Blocked,
    int Down,
    int Silent,
    int Unknown,
    double? Uptime,
    NodeAvailabilityPoint[] Series);

/// <param name="Handshakes">
/// Rounds in which the handshake completed, reachable or not. Beside <paramref name="Unreachable"/>
/// it says which block this is: handshakes that complete and carry nothing are DPI, handshakes
/// that never complete are the address or the port.
/// </param>
/// <param name="Reachability">
/// The share of rounds that got traffic through, 0 to 1, counting only the rounds the probe
/// actually ran: a probe that could not run its own check says nothing about the node. Null when
/// it never ran one.
/// </param>
public sealed record ProbeNodeAvailabilityResponse(
    string NodeId,
    int Reachable,
    int Unreachable,
    int Errors,
    int Handshakes,
    double? Reachability);

/// <summary>One probe's own view of the fleet: which nodes it reaches from where it stands.</summary>
public sealed record ProbeAvailabilityResponse(
    string ProbeId,
    string Name,
    bool Revoked,
    ProbeNodeAvailabilityResponse[] Nodes);

/// <summary>
/// How the fleet stood over a window. Per node as the panel judged it, and per probe as each
/// vantage point saw it - the second is what names the network a block is in, which the combined
/// verdict cannot.
/// </summary>
/// <param name="Bucket">"hour" or "day": how wide one point of a node series is.</param>
/// <param name="RetentionDays">How long history is kept. Zero means nothing is being recorded.</param>
/// <param name="RecordingSince">The oldest hour still kept, or null when nothing has been recorded.</param>
public sealed record AvailabilitySummaryResponse(
    string Window,
    string Bucket,
    DateTimeOffset From,
    DateTimeOffset To,
    NodeAvailabilityResponse[] Nodes,
    ProbeAvailabilityResponse[] Probes,
    int RetentionDays,
    DateTimeOffset? RecordingSince);

/// <summary>
/// What a cleared availability history left behind.
/// </summary>
/// <param name="NodeId">The node that was cleared, or null when the whole fleet was.</param>
/// <param name="Dropped">
/// Rows dropped, across both tables. Worth returning rather than a bare 204: it is the only
/// confirmation an operator gets that the numbers they were looking at are the ones that went.
/// </param>
public sealed record AvailabilityResetResponse(string? NodeId, int Dropped);

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
/// Who is signed in, and under what.
/// </summary>
/// <param name="Demo">
/// True for the read-only demo account. The panel refuses its writes whatever the page does, so
/// this is not what enforces anything - it is what lets the frame say so, which is the difference
/// between a panel that is read-only and a panel that merely appears broken.
/// </param>
public sealed record SessionResponse(string Username, bool Demo);

/// <summary>
/// A freshly generated AmneziaWG 3.x header protection key. Returned for the operator to paste
/// into the profile rather than stored on the side: it only takes effect once the profile is
/// saved, and a key nobody saved should leave no trace.
/// </summary>
public sealed record GeneratedKeyResponse(string Key);

/// <summary>How one panel of a pair reads at a glance. A deliberately short vocabulary: an
/// operator deciding whether to switch has to be able to hold all of it in their head.</summary>
public static class PanelStates
{
    /// <summary>The panel the fleet answers to.</summary>
    public const string Active = "active";

    /// <summary>A standby that is in touch and holds the fleet state.</summary>
    public const string Replicating = "replicating";

    /// <summary>Enrolled, in touch, and has never applied a snapshot - so it cannot take over.</summary>
    public const string NeverSynced = "never_synced";

    /// <summary>Has not been heard from for longer than the grace period.</summary>
    public const string Silent = "silent";

    /// <summary>Cut off on purpose. It receives nothing and may not promote itself.</summary>
    public const string Revoked = "revoked";
}

/// <param name="IsSelf">True for the panel answering the request, so the page can say "this one"
/// without the caller having to compare ids.</param>
/// <param name="LagSeconds">
/// How stale this panel's copy of the fleet is: seconds since it last confirmed it was current.
/// Null on the panel in charge, which is the source rather than a copy of anything, and on a
/// replica that has never reported in.
///
/// Deliberately not measured from <see cref="SnapshotAt"/>. A snapshot is re-fetched only when
/// the fleet actually changes, so on a quiet fleet the moment it was published ages without
/// bound while the replica is perfectly up to date - reading that out as "behind" sends an
/// operator looking for a replication fault that is not there, and it is the number they check
/// before deciding whether this panel is safe to promote.
/// </param>
public sealed record PanelResponse(
    string Id,
    string Name,
    string? Hostname,
    string? Url,
    string? PublicIp,
    string? Version,
    string Role,
    long Term,
    bool IsSelf,
    bool IsActive,
    bool Revoked,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? SnapshotAt,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    int? LagSeconds,
    string State,
    string StateReason)
{
    public static PanelResponse From(
        PanelRecord panel,
        string selfId,
        string? activePanelId,
        DateTimeOffset? selfSnapshotAt,
        DateTimeOffset? selfSyncedAt,
        TimeSpan grace,
        DateTimeOffset now)
    {
        var isSelf = string.Equals(panel.Id, selfId, StringComparison.Ordinal);

        // The panel answering knows its own applied snapshot exactly; for its peer it only knows
        // what the peer last reported, which is the honest difference between the two columns.
        var snapshotAt = isSelf ? selfSnapshotAt ?? panel.SnapshotAt : panel.SnapshotAt;
        var isActive = string.Equals(panel.Id, activePanelId, StringComparison.Ordinal);
        var silent = panel.LastSeenAt is null || now - panel.LastSeenAt > grace;

        // When this panel last established that it held the current fleet - a pull that either
        // applied a snapshot or was answered 304. For itself that is its own clock; for a peer it
        // is the last time the peer reported in, which is the same fact seen from one side further
        // away.
        var confirmedAt = isSelf ? selfSyncedAt : panel.LastSeenAt;

        var state = panel.Revoked ? PanelStates.Revoked
            : isActive ? PanelStates.Active
            : silent ? PanelStates.Silent
            : snapshotAt is null ? PanelStates.NeverSynced
            : PanelStates.Replicating;

        return new PanelResponse(
            panel.Id,
            panel.Name,
            panel.Hostname,
            panel.Url,
            panel.PublicIp,
            panel.AgentVersion,
            panel.Role,
            panel.Term,
            isSelf,
            isActive,
            panel.Revoked,
            panel.EnrolledAt,
            snapshotAt,
            panel.LastSeenAt,
            panel.LastError,
            isActive || confirmedAt is null
                ? null
                : (int)Math.Max(0, Math.Round((now - confirmedAt.Value).TotalSeconds)),
            state,
            Reason(state, panel, confirmedAt, now));
    }

    private static string Reason(string state, PanelRecord panel, DateTimeOffset? confirmedAt, DateTimeOffset now) => state switch
    {
        PanelStates.Revoked => "Cut off on purpose: it receives no configuration and cannot take over.",
        PanelStates.Active => $"In charge of the fleet at term {panel.Term}.",
        PanelStates.Silent => panel.LastSeenAt is { } seen
            ? $"Nothing heard from it for {PanelFailoverPolicy.Describe(now - seen)}."
            : "Enrolled, but it has never reported in.",
        PanelStates.NeverSynced => "In touch, but it has not applied a snapshot yet, so it holds no fleet state and cannot take over.",
        _ => confirmedAt is { } confirmed
            ? $"Replicating; last confirmed current {PanelFailoverPolicy.Describe(now - confirmed)} ago."
            : "Replicating."
    };
}

/// <summary>
/// The pair as the panel answering sees it: which role it is in, how its replication is going,
/// and every panel it knows about.
/// </summary>
/// <param name="Role">This panel's live role - read from its database, never from its environment.</param>
/// <param name="HasFleetState">
/// Whether this panel has ever applied a snapshot. False on a standby means promotion is refused
/// outright: a panel with no fleet state would publish an empty one, and every node would
/// converge onto it and drop every peer it serves.
/// </param>
/// <param name="Held">The operator's brake. A held standby never promotes itself, however armed.</param>
/// <param name="WitnessOnline">Whether this panel could reach anything outside the pair, or null
/// while it has had no reason to check.</param>
/// <param name="RecordName">The record agents follow to reach the panel, or null when there is
/// none and the operator points them by hand.</param>
public sealed record PanelPairResponse(
    string Role,
    string PanelId,
    string Name,
    long Term,
    string? ActivePanelId,
    DateTimeOffset? PromotedAt,
    string Mode,
    bool Held,
    bool Enrolled,
    bool HasFleetState,
    DateTimeOffset? SyncedAt,
    DateTimeOffset? SnapshotAt,
    string? SyncError,
    int SyncIntervalSeconds,
    int GraceSeconds,
    int CooldownSeconds,
    bool WitnessConfigured,
    bool? WitnessOnline,
    string Provider,
    bool ProviderConfigured,
    string? RecordName,
    string? Url,
    string? PublicIp,
    DateTimeOffset? EvaluatedAt,
    string? Action,
    string? Message,
    PanelResponse[] Panels,
    string? Warning);

/// <summary>What a standby panel is to be enrolled as. Only a name: everything else about a panel
/// is in its own environment, because a panel is not configured from here.</summary>
public sealed record CreatePanelRequest(string Name);

public sealed record UpdatePanelFailoverRequest(string Mode);

/// <summary>The brake, and the one write a standby accepts about itself.</summary>
public sealed record PanelHoldRequest(bool Held);

/// <summary>What a promotion or a demotion did, beside the pair it left behind.</summary>
public sealed record PanelSwitchResponse(long Term, string? Warning, PanelPairResponse Pair);
