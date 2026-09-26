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

public sealed record NodeStatusReport(
    long AppliedRevision,
    bool InterfaceUp,
    string? Backend,
    string AgentVersion,
    DateTimeOffset ReportedAt,
    PeerStatus[] Peers,
    NodeMetrics? Metrics,
    string? LastError,
    int BundleSchemaVersion = 0);

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
    public static int Normalize(int reported)
        => reported <= 0
            ? DesiredStateBundle.MinimumSupportedSchemaVersion
            : Math.Min(reported, DesiredStateBundle.CurrentSchemaVersion);
}
