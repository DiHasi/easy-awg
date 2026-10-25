namespace AwgEasy.Control;

/// <summary>
/// The window after a rotation in which the fleet cannot be observed - so it is not recorded.
///
/// A probe re-renders its config only when it fetches its next assignment, so for up to one
/// <see cref="FailoverOptions.ProbeInterval"/> after the identity was replaced every probe is
/// still speaking the previous wire format with the previous preshared key. No handshake completes
/// anywhere and the whole fleet reads as blocked, including the nodes that have already converged.
/// The handshake timeout is added on top because a probe that began its round just before the
/// rotation can still be waiting on one when it reports.
///
/// Three readers, one rule, which is why it lives here rather than inline at any of them:
/// <list type="bullet">
/// <item><see cref="FailoverMonitor"/> withholds newly-failing transitions, so the rotation does
/// not page an operator about the fleet they are standing over.</item>
/// <item>The node sample for that round is not written, because the round judged the previous
/// identity's probes rather than the nodes.</item>
/// <item>Neither is the probe round, for the same reason - what it saw was the rotation.</item>
/// </list>
///
/// Nothing is recorded in place of it: the history counts samples, and a bucket with no samples
/// already means "no evidence" rather than "not healthy", which is exactly the panel's position
/// here. Recording only the rounds that came out well would be worse than recording none, because
/// every share is taken over the samples that exist - a window filtered down to its successes
/// claims an uptime the evidence does not support.
///
/// The grace period <see cref="FailoverPolicy"/> waits out is the other half of this and has to
/// outlast a probe round for the same reason (<c>AWG_FAILOVER_GRACE_SECONDS</c> above
/// <c>AWG_PROBE_INTERVAL_SECONDS</c>); this one keeps the record of the window honest, it does not
/// decide anything about where traffic goes.
/// </summary>
public static class RotationSettling
{
    /// <param name="identityRotatedAt">
    /// When the identity was last replaced, or null on a fleet that has never been rotated - which
    /// is not settling, and is the common case.
    /// </param>
    /// <param name="now">The panel's clock, the same one that stamps the evidence.</param>
    public static bool IsSettling(DateTimeOffset? identityRotatedAt, DateTimeOffset now, FailoverOptions failover)
        => identityRotatedAt is { } rotatedAt
            && now - rotatedAt <= failover.ProbeInterval + failover.ProbeHandshakeTimeout;
}
