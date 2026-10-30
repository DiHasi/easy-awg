namespace AwgEasy.Contracts;

/// <summary>
/// Day-0 enrollment. The agent generates its own key pair locally and only ever sends the
/// public half, so the credential that authenticates it never crosses the wire.
/// </summary>
/// <param name="EnrollmentToken">Single-use, short-lived token issued by the control plane UI.</param>
/// <param name="Hostname">Reported for operator convenience only; never trusted for authorization.</param>
/// <param name="AgentPublicKey">Base64url SubjectPublicKeyInfo of the ECDSA P-256 key generated on the node.</param>
/// <param name="BundleSchemaVersion">
/// Highest bundle schema this agent understands. Reported here and on every status report so the
/// control plane never hands a node a bundle carrying settings its AmneziaWG build would reject.
/// Absent (0) from agents that predate schema negotiation and read as 1.
/// </param>
public sealed record EnrollRequest(
    string EnrollmentToken,
    string Hostname,
    string AgentPublicKey,
    string AgentVersion,
    int BundleSchemaVersion = 0);

/// <param name="ControlSigningPublicKey">Pinned by the agent and used to verify every bundle afterwards.</param>
public sealed record EnrollResponse(
    string NodeId,
    string ControlSigningPublicKey,
    string ControlSigningKeyId);

/// <param name="PublicIp">
/// The address this node believes it is reachable at, discovered from outside. The control plane
/// uses it as the target of the DNS record clients follow, so a node that could not determine it
/// reports null rather than a guess. Unlike the bundle this report is not signed content shared
/// byte for byte, so an older agent simply omits the field - no fleet-wide upgrade is involved.
/// </param>
/// <param name="EgressAddresses">
/// Every address the node's egress interface carries. Null means this round could not enumerate
/// them and the panel keeps what it had; an empty array means it enumerated and found none. The
/// two are not the same thing, and conflating them would forget a node's addresses over one
/// failed lookup.
/// </param>
public sealed record NodeStatusReport(
    long AppliedRevision,
    bool InterfaceUp,
    string? Backend,
    string AgentVersion,
    DateTimeOffset ReportedAt,
    PeerStatus[] Peers,
    NodeMetrics? Metrics,
    string? LastError,
    int BundleSchemaVersion = 0,
    string? PublicIp = null,
    ReportedEgressAddress[]? EgressAddresses = null);

/// <summary>
/// One address the node's egress interface carries, and what the outside makes of it.
///
/// Which addresses a node holds is a thing only the node can answer, so the panel offers an
/// operator exactly what was reported rather than a field to type an address into: SNAT to an
/// address the host does not hold leaves that peer with no traffic at all.
/// </summary>
/// <param name="KernelDefault">
/// True for the address the kernel picks right now for outbound traffic. That is what unpinned
/// peers leave with while the node has no default of its own, and until this field existed it was
/// not visible anywhere - on a host with several addresses on one interface it is decided by the
/// order they happen to sit in.
/// </param>
/// <param name="Observed">
/// What an echo service answered when the request was bound to this source address, or null when
/// the check did not run or did not answer. The difference between "this address is mine" and
/// "this address presents as something else", which is the whole question behind pinning one.
/// </param>
public sealed record ReportedEgressAddress(
    string Address,
    bool KernelDefault = false,
    string? Observed = null);

/// <summary>
/// Traffic counters are reported per public key. The node does not know which client a key
/// belongs to; the control plane owns that mapping.
/// </summary>
public sealed record PeerStatus(
    string PublicKey,
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes);

public sealed record NodeMetrics(
    double LoadAverage1m,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long DiskTotalBytes,
    long DiskAvailableBytes,
    TimeSpan Uptime);

/// <summary>Control-plane reply to a status report, carrying the revision the agent should converge to.</summary>
public sealed record NodeStatusAck(
    long CurrentRevision,
    bool BundleAvailable);

/// <summary>
/// Clamps a reported bundle schema version into the window this build can serve. Agents that
/// predate schema negotiation send nothing and must be read as schema 1, not as 0.
/// </summary>
public static class BundleSchema
{
    /// <summary>
    /// The schema at which a node became able to serve the AmneziaWG 3.x wire format - header
    /// protection, the padding and timing ranges, random trailers.
    ///
    /// Deliberately not <see cref="DesiredStateBundle.CurrentSchemaVersion"/>: what a node needs
    /// in order to carry a 3.x fleet is this version, and a later schema adds other things
    /// entirely. Comparing against the newest one worked only while they were the same number,
    /// and the first bump past it would have downgraded the profile of every node that was in
    /// fact serving it perfectly well.
    /// </summary>
    public const int Schema3ObfuscationVersion = 2;

    public static int Normalize(int reported)
        => reported <= 0
            ? DesiredStateBundle.MinimumSupportedSchemaVersion
            : Math.Min(reported, DesiredStateBundle.CurrentSchemaVersion);
}
