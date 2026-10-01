using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public static class NodeHealthStates
{
    /// <summary>Clients can reach it: a probe completed a handshake, or with no probe watching, the
    /// agent reports its tunnel up.</summary>
    public const string Healthy = "healthy";

    /// <summary>
    /// The agent reports the tunnel up and still no probe completes a handshake. The node is fine;
    /// the path from where clients are is not. This is the case failover exists for, and the one a
    /// node can never detect about itself.
    /// </summary>
    public const string Blocked = "blocked";

    /// <summary>The tunnel is down: the agent says so, or nobody can reach the node at all.</summary>
    public const string Down = "down";

    /// <summary>No status report for a while and no probe watching. Could be the node, could be the
    /// path between it and the panel; either way nothing vouches for it.</summary>
    public const string Silent = "silent";

    /// <summary>Has not reported yet.</summary>
    public const string Unknown = "unknown";

    public const string Revoked = "revoked";

    public static bool IsFailing(string state) => state is Blocked or Down or Silent;
}

/// <param name="Source">"probes" when probe results decided the state, "agent" when only the node's own reports did.</param>
/// <param name="LastGoodAt">When the node was last known to be serving.</param>
/// <param name="FailingSince">
/// When the current failure began, as far as the evidence knows - what the grace period counts
/// from. Null while the node is not failing.
/// </param>
public sealed record NodeAssessment(
    string NodeId,
    string State,
    string Reason,
    string Source,
    DateTimeOffset? LastGoodAt,
    DateTimeOffset? FailingSince,
    int ProbesReachable,
    int ProbesReporting)
{
    public bool IsFailing => NodeHealthStates.IsFailing(State);

    public bool ProbeVerified => Source == NodeHealthEvaluator.ProbeSource && ProbesReachable > 0;
}

/// <summary>
/// Decides what state a node is in from two kinds of evidence that answer different questions.
///
/// The agent's own reports say whether the node is running: the tunnel is up, the config applied.
/// They cannot say whether clients reach it - a node whose address is blocked upstream reports
/// healthy forever. Probe results answer that, from where clients stand, but a probe cannot tell
/// a blocked node from a dead one. Put together they can, and that difference is what decides
/// whether an operator needs a new node or just a new address.
///
/// When probes have something current to say, they decide reachability; the agent only decides
/// between blocked and down. With no probe watching, the agent is all there is, and a block goes
/// unnoticed - the reason says so rather than claiming more than is known.
/// </summary>
public static class NodeHealthEvaluator
{
    public const string ProbeSource = "probes";
    public const string AgentSource = "agent";

    public static NodeAssessment Assess(
        NodeRecord node,
        IEnumerable<ProbeObservation> observations,
        DateTimeOffset now,
        FailoverOptions options)
    {
        if (node.Revoked)
        {
            return new(node.Id, NodeHealthStates.Revoked, "Revoked: no longer receives configuration.", AgentSource, null, null, 0, 0);
        }

        var agentFresh = node.LastSeenAt is { } seen && now - seen <= options.AgentStaleAfter;
        var tunnelUp = agentFresh && node.InterfaceUp;

        // Only current results about the address the node has now. A probe error says nothing
        // about the node, and a result about an old address says nothing about the new one.
        var evidence = observations
            .Where(observation => observation.NodeId == node.Id
                && observation.Outcome != ProbeOutcomes.Error
                && now - observation.CheckedAt <= options.ProbeStaleAfter
                && node.PublicIp is not null
                && string.Equals(observation.Address, node.PublicIp, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (evidence.Length > 0)
        {
            var reachable = evidence.Count(observation => observation.Outcome == ProbeOutcomes.Reachable);
            var lastGoodAt = evidence.Max(observation => observation.LastReachableAt);
            var of = $"{Count(reachable)} of {Count(evidence.Length)} probe(s)";

            if (reachable > 0)
            {
                return new(node.Id, NodeHealthStates.Healthy, $"{of} got traffic through the tunnel.", ProbeSource, lastGoodAt, null, reachable, evidence.Length);
            }

            // Failing means every probe fails, so the failure began when the last of them started
            // failing. A probe enrolled after a node was already blocked therefore starts the
            // clock when it first looks, rather than declaring the node failed since forever.
            var failingSince = evidence.Max(observation => observation.FailingSince ?? observation.CheckedAt);

            if (tunnelUp)
            {
                // Which kind of block it is changes what the operator does next: a dropped
                // handshake is the address or the port, a handshake followed by silence is DPI
                // recognising the traffic - or the node's own egress.
                var handshakes = evidence.Count(observation => observation.Handshake == true);
                return new(
                    node.Id,
                    NodeHealthStates.Blocked,
                    handshakes > 0
                        ? $"The agent reports the tunnel up and {Count(handshakes)} of {Count(evidence.Length)} probe(s) completed a handshake, "
                          + "but no traffic came back through it: the pattern of DPI blocking, or the node's egress is broken."
                        : $"The agent reports the tunnel up, but none of {Count(evidence.Length)} probe(s) completed a handshake: "
                          + "the address or the port is likely blocked between clients and this node.",
                    ProbeSource,
                    lastGoodAt,
                    failingSince,
                    0,
                    evidence.Length);
            }

            return new(
                node.Id,
                NodeHealthStates.Down,
                agentFresh
                    ? "The agent reports the tunnel down, and no probe completed a handshake."
                    : "Neither the agent nor any probe has reached this node.",
                ProbeSource,
                lastGoodAt,
                failingSince,
                0,
                evidence.Length);
        }

        if (node.LastSeenAt is null)
        {
            return new(node.Id, NodeHealthStates.Unknown, "Has not reported yet.", AgentSource, null, null, 0, 0);
        }

        if (tunnelUp)
        {
            return new(
                node.Id,
                NodeHealthStates.Healthy,
                "The agent reports the tunnel up. No probe checks this node, so a block from the client side would go unnoticed.",
                AgentSource,
                node.LastSeenAt,
                null,
                0,
                0);
        }

        // Without probes the last report that had the tunnel up is the best guess at when things
        // went wrong. A node that never had it up has been failing since it first reported.
        var agentFailingSince = node.LastUpAt ?? node.LastSeenAt;

        if (agentFresh)
        {
            return new(node.Id, NodeHealthStates.Down, "The agent reports the tunnel down.", AgentSource, node.LastUpAt, agentFailingSince, 0, 0);
        }

        return new(
            node.Id,
            NodeHealthStates.Silent,
            $"No status report since {node.LastSeenAt.Value.ToString("u", CultureInfo.InvariantCulture)}, and no probe checks this node.",
            AgentSource,
            node.LastUpAt,
            agentFailingSince,
            0,
            0);
    }

    public static IReadOnlyDictionary<string, NodeAssessment> AssessAll(
        IEnumerable<NodeRecord> nodes,
        IReadOnlyList<ProbeObservation> observations,
        DateTimeOffset now,
        FailoverOptions options)
        => nodes.ToDictionary(node => node.Id, node => Assess(node, observations, now, options), StringComparer.Ordinal);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
