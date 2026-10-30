namespace AwgEasy.Control;

// The contract between two panels.
//
// It lives here rather than in AwgEasy.Contracts on purpose. Contracts is the wire format shared
// with the agent, and changing it changes the fleet; this is spoken only between two copies of
// the control plane, it carries the fleet private key and every client private key, and it would
// otherwise be compiled into the AOT-published agent that must never hold any of them.
//
// Both ends are the same build in normal operation, so the payloads are signed and verified
// exactly as a bundle is - against the bytes as received, never re-serialized - and both sides go
// through ControlJsonContext, so they cannot disagree about what was signed.

public static class PanelRoles
{
    /// <summary>The panel the fleet answers to: it serves bundles, allocates addresses, moves DNS.</summary>
    public const string Active = "active";

    /// <summary>A replica. It reads, it replicates, and it refuses every write until it is promoted.</summary>
    public const string Standby = "standby";

    public static bool IsKnown(string? role) => role is Active or Standby;
}

/// <summary>
/// Whether a standby may promote itself. Stored with the fleet and replicated, like node failover
/// mode, so an operator arms the pair once from the panel that is in charge - and a standby that
/// has lost contact still knows what it was told.
/// </summary>
public static class PanelFailoverModes
{
    public const string Manual = "manual";
    public const string Automatic = "automatic";

    public static bool IsKnown(string? mode) => mode is Manual or Automatic;
}

/// <summary>
/// The envelope every panel-to-panel payload travels in: the bytes, a signature over them, and
/// the id of the fleet signing key that made it.
///
/// Signed with the fleet signing key, which a standby pins at enrollment exactly as an agent
/// does. A standby holds the whole panel, so what it accepts as the fleet's state has to be
/// provably from the panel that issued its identity, and not merely from whatever answered the
/// hostname it was pointed at.
/// </summary>
public sealed record SignedPanelPayload(string Payload, string Signature, string KeyId);

/// <summary>What a standby must agree with the active about before it will apply anything.</summary>
public static class PanelSchema
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// The oldest payload this build will apply. A pair is upgraded one panel at a time and the
    /// active goes first, so a standby one release behind may be handed something it does not
    /// understand: it refuses, keeps the state it already has, and says to upgrade it - the same
    /// fail-static answer a node gives when the panel is unreachable.
    /// </summary>
    public const int MinimumVersion = 1;

    public static bool CanApply(int version) => version is >= MinimumVersion and <= CurrentVersion;
}

/// <summary>
/// What one panel tells another about itself in a single cheap, signed call. The term is the whole
/// point: it is how a panel that lost a promotion finds out, and the only thing either side needs
/// in order to agree on which of them is in charge.
/// </summary>
public sealed record PanelHello(
    string PanelId,
    string Name,
    string Role,
    long Term,
    string? ActivePanelId,
    DateTimeOffset? SnapshotAt,
    string Version,
    long Revision);

/// <summary>
/// What a standby reports on every pull, so the active's own panel can say how far behind its
/// replica is without having to go and ask.
/// </summary>
/// <param name="Url">Where this panel can be reached. Reported rather than taken from the
/// request's source address, for the same reason a node reports its own public address: a proxy
/// in front of the panel would otherwise have the pair watching the proxy.</param>
public sealed record PanelStatusReport(
    string Role,
    long Term,
    string Version,
    string? Url,
    string? PublicIp,
    DateTimeOffset? SnapshotAt,
    string? LastError);

/// <param name="PeerUrl">Where the panel that issued this identity can be reached, so a standby
/// does not depend on the URL it happened to be enrolled through staying correct.</param>
public sealed record PanelEnrollResponse(
    string PanelId,
    string ControlSigningPublicKey,
    string ControlSigningKeyId,
    string ActivePanelName,
    long Term,
    string? PeerUrl);

// ---------------------------------------------------------------------------------------------
// The snapshot: everything the fleet runs on.
//
// Pulled whole rather than as a diff, and gated by an ETag over its own bytes, so there is no
// per-write bookkeeping anywhere in the panel that somebody has to remember to bump. The rows
// are carried as rows - the obfuscation profile as the JSON the column holds, not as a reparsed
// object - so what the standby stores is a faithful copy of what the active stores, and a
// serializer change on one side cannot quietly rewrite the fleet on the other.
// ---------------------------------------------------------------------------------------------

/// <param name="PanelId">Who this snapshot was built for. Checked on arrival: a correctly signed
/// payload is still not necessarily one this panel may apply.</param>
/// <param name="CreatedAt">
/// The active's stamp, and the monotonicity anchor. A standby refuses a snapshot older than the
/// one it has applied, which is what stops a demoted panel - which still holds the signing key -
/// from handing a standby the state it had before it lost.
/// </param>
/// <param name="ExpiresAt">Replay protection, enforced because this arrives over the network.</param>
public sealed record PanelSnapshot(
    int SchemaVersion,
    string PanelId,
    long Term,
    string? ActivePanelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PanelFleetState Fleet,
    PanelPeerRow[] Panels,
    PanelClientRow[] Clients,
    PanelGroupRow[] Groups,
    PanelNodeConfigRow[] Nodes,
    PanelProbeConfigRow[] Probes,
    PanelAdminRow[] Admins,
    PanelTokenRow[] Tokens,
    PanelShareRow[] Shares,
    PanelBaselineRow[] Baselines);

/// <summary>
/// The fleet row as it stands, including the fleet private key and the bundle signing key pair: a
/// promoted panel has to serve bundles the agents already pinned a signing key for, and answer
/// handshakes from configs that pin the server public key.
/// </summary>
public sealed record PanelFleetState(
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
    string? ObfuscationJson,
    long Revision,
    long IdentityRevision,
    DateTimeOffset? IdentityRotatedAt,
    string? ActiveNodeId,
    DateTimeOffset? ActiveNodeSetAt,
    string FailoverMode,
    long PanelTerm,
    string? ActivePanelId,
    DateTimeOffset? PanelPromotedAt,
    string PanelFailoverMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PanelPeerRow(
    string Id,
    string Name,
    string? Hostname,
    string AgentPublicKey,
    bool Revoked,
    DateTimeOffset EnrolledAt);

public sealed record PanelClientRow(
    string Id,
    string Name,
    string Address,
    string PrivateKey,
    string PublicKey,
    string PresharedKey,
    bool Enabled,
    string? ObfuscationJson,
    string Kind,
    string? GroupId,
    int SortOrder,
    int ConfigGeneration,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? EgressLabel = null);

public sealed record PanelGroupRow(
    string Id,
    string Name,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The half of a node row that is configuration. Deliberately not the half the agent reports -
/// status, applied revision, public address and the rest arrive on the live stream, because a
/// snapshot whose ETag changed every twenty seconds would be re-pulled whole on every poll.
/// </summary>
public sealed record PanelNodeConfigRow(
    string Id,
    string Name,
    string? Hostname,
    string? EndpointHost,
    string AgentPublicKey,
    string? EgressInterface,
    int? Mtu,
    bool Revoked,
    int FailoverPriority,
    bool AutoFailover,
    DateTimeOffset EnrolledAt,
    string? EgressPlan = null);

public sealed record PanelProbeConfigRow(
    string Id,
    string Name,
    string? Hostname,
    string AgentPublicKey,
    string ClientId,
    bool Revoked,
    DateTimeOffset EnrolledAt);

/// <summary>Admin accounts, as hashes. The row id is left behind: nothing references it, and
/// reassigning it on the replica is cheaper than carrying an autoincrement between panels.</summary>
/// <param name="IsDemo">
/// Whether this is the read-only demo account. Optional, with a default, so it needs no schema
/// bump: an older standby ignores the property, and a newer one reading an older active's
/// snapshot cannot lose a flag the active does not have - the column arrived with it. A pair is
/// upgraded active-first, which is the order that makes that true.
/// </param>
public sealed record PanelAdminRow(string Username, string PasswordHash, DateTimeOffset CreatedAt, bool IsDemo = false);

/// <summary>Enrollment tokens travel too, so a token issued moments before a switch is still good
/// on the panel that took over.</summary>
public sealed record PanelTokenRow(
    string TokenHash,
    string NodeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    string? UsedByNodeId,
    string Kind,
    string? EndpointHost,
    string? EgressInterface,
    int? Mtu);

public sealed record PanelShareRow(
    string TokenHash,
    string ClientId,
    int Generation,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A manual counter reset is operator intent, not an observation, so it belongs with the
/// configuration rather than on the live stream.</summary>
public sealed record PanelBaselineRow(
    string NodeId,
    string PublicKey,
    long ReceivedBytes,
    long TransmittedBytes,
    DateTimeOffset ResetAt);

// ---------------------------------------------------------------------------------------------
// The live stream: what the panel observed.
//
// Two different things in one call. The latest-state tables are small and are replaced wholesale
// every poll; the histories are append-mostly and are read from a cursor, so a standby enrolled
// into a fleet with ninety days behind it catches up over several polls rather than in one
// enormous response.
// ---------------------------------------------------------------------------------------------

public sealed record PanelStream(
    int SchemaVersion,
    string PanelId,
    long Term,
    string? ActivePanelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PanelNodeStateRow[] Nodes,
    PanelProbeStateRow[] Probes,
    PanelPeerStateRow[] Panels,
    PanelPeerStatRow[] PeerStats,
    PanelProbeResultRow[] ProbeResults,
    PanelUsageBatch Usage,
    PanelNodeAvailabilityBatch NodeAvailability,
    PanelProbeAvailabilityBatch ProbeAvailability);

public sealed record PanelNodeStateRow(
    string Id,
    string? AgentVersion,
    string Status,
    long AppliedRevision,
    bool InterfaceUp,
    string? Backend,
    int BundleSchemaVersion,
    string? PublicIp,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    DateTimeOffset? LastUpAt,
    string? EgressReport = null);

public sealed record PanelProbeStateRow(string Id, string? AgentVersion, DateTimeOffset? LastSeenAt);

public sealed record PanelPeerStateRow(
    string Id,
    string? Url,
    string? PublicIp,
    string? AgentVersion,
    string Role,
    long Term,
    DateTimeOffset? SnapshotAt,
    DateTimeOffset? LastSeenAt,
    string? LastError);

public sealed record PanelPeerStatRow(
    string NodeId,
    string PublicKey,
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes,
    DateTimeOffset ReportedAt);

public sealed record PanelProbeResultRow(
    string ProbeId,
    string NodeId,
    string Address,
    string Outcome,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastReachableAt,
    DateTimeOffset? FailingSince,
    bool? Handshake,
    int? LatencyMs,
    string? Detail);

/// <param name="Cursor">The <c>updated_at</c> to resume strictly after. Every row at that exact
/// stamp is in this batch already - the active extends a full batch to the end of the timestamp
/// it stopped in - so resuming after it can neither skip a row nor stall on a crowded instant.</param>
/// <param name="More">Whether further history is waiting. The next poll takes the next batch;
/// nothing else waits on a standby finishing its catch-up.</param>
public sealed record PanelUsageBatch(PanelUsageRow[] Rows, string? Cursor, bool More);

public sealed record PanelUsageRow(
    string ClientId,
    DateTimeOffset BucketStart,
    long ReceivedBytes,
    long TransmittedBytes,
    DateTimeOffset UpdatedAt);

public sealed record PanelNodeAvailabilityBatch(PanelNodeAvailabilityRow[] Rows, string? Cursor, bool More);

public sealed record PanelNodeAvailabilityRow(
    string NodeId,
    DateTimeOffset BucketStart,
    int Samples,
    int Healthy,
    int Blocked,
    int Down,
    int Silent,
    int Unknown,
    DateTimeOffset UpdatedAt);

public sealed record PanelProbeAvailabilityBatch(PanelProbeAvailabilityRow[] Rows, string? Cursor, bool More);

public sealed record PanelProbeAvailabilityRow(
    string ProbeId,
    string NodeId,
    DateTimeOffset BucketStart,
    int Reachable,
    int Unreachable,
    int Errors,
    int Handshakes,
    DateTimeOffset UpdatedAt);
