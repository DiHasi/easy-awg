namespace AwgEasy.Contracts;

// The probe protocol. A probe is the same binary as the node agent run with AWG_ROLE=probe, placed
// where clients are rather than where nodes are, and it answers the one question a node cannot
// answer about itself: can a client actually complete a handshake with it from out there.
//
// It enrolls with the same EnrollRequest/EnrollResponse and signs its requests the same way, but
// it never receives a bundle. A probe usually sits inside the network that does the blocking, so
// it must not hold the fleet private key; it holds a client key of its own and nothing else.

/// <summary>What one probe is asked to check, rebuilt from the fleet on every fetch.</summary>
/// <param name="IntervalSeconds">How long the probe waits between rounds.</param>
/// <param name="HandshakeTimeoutSeconds">How long one target has to complete a handshake.</param>
/// <param name="CheckUrls">
/// Fetched through the tunnel once the handshake completes; any answer from any of them means
/// traffic flows. Empty checks the handshake alone, which misses the most common block.
/// </param>
/// <param name="TrafficTimeoutSeconds">How long each of <paramref name="CheckUrls"/> has to answer.</param>
public sealed record ProbeAssignment(
    string ProbeId,
    int IntervalSeconds,
    int HandshakeTimeoutSeconds,
    ProbeTarget[] Targets,
    string[]? CheckUrls = null,
    int TrafficTimeoutSeconds = 0);

/// <param name="Address">The node's public address, exactly as the panel would point DNS at it.</param>
/// <param name="Config">
/// A complete awg-quick config for checking this node: the probe's own client key, the fleet
/// wire format, the node's address as the endpoint, AllowedIPs 0.0.0.0/0 and no routes. Rendered
/// by the panel so the probe never has to agree with it on how obfuscation settings are written.
/// </param>
public sealed record ProbeTarget(
    string NodeId,
    string Address,
    int Port,
    string Config);

public sealed record ProbeReport(
    string AgentVersion,
    DateTimeOffset ReportedAt,
    ProbeResult[] Results);

/// <param name="Address">The address that was checked, so a result about a node's old address is
/// not mistaken for one about its current address.</param>
/// <param name="Outcome">One of <see cref="ProbeOutcomes"/>.</param>
/// <param name="LatencyMs">Round trip of the request through the tunnel, or time to the handshake when there was none. Coarse.</param>
/// <param name="Handshake">
/// Whether the handshake completed, whatever happened after it. True with
/// <see cref="ProbeOutcomes.Unreachable"/> is the signature of DPI blocking: the handshake gets
/// through and every packet after it is dropped. Null when the probe could not tell.
/// </param>
public sealed record ProbeResult(
    string NodeId,
    string Address,
    string Outcome,
    DateTimeOffset CheckedAt,
    int? LatencyMs,
    string? Detail,
    bool? Handshake = null);

public static class ProbeOutcomes
{
    /// <summary>
    /// A handshake completed and a request through the tunnel was answered: a client standing
    /// where the probe stands would be served.
    /// </summary>
    public const string Reachable = "reachable";

    /// <summary>
    /// No handshake came back, or one did and then no traffic passed - see
    /// <see cref="ProbeResult.Handshake"/> for which.
    /// </summary>
    public const string Unreachable = "unreachable";

    /// <summary>
    /// The probe could not run the check at all - its own tooling failed. Says nothing about the
    /// node, and the panel ignores it, so a broken probe can never trigger a failover.
    /// </summary>
    public const string Error = "error";

    public static bool IsKnown(string? outcome) => outcome is Reachable or Unreachable or Error;
}
