using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public static class FailoverModes
{
    /// <summary>The panel watches and tells; an operator decides.</summary>
    public const string Manual = "manual";

    /// <summary>The panel moves the record itself once the active node has failed long enough.</summary>
    public const string Automatic = "automatic";

    public static bool IsKnown(string? mode) => mode is Manual or Automatic;
}

public enum FailoverAction
{
    /// <summary>Nothing to do: the active node is fine, or there is no active node to protect.</summary>
    None,

    /// <summary>The active node is failing, but not for long enough yet.</summary>
    Wait,

    /// <summary>The active node has failed long enough, but the last switch was too recent.</summary>
    Hold,

    /// <summary>The active node has failed long enough and no node can take over.</summary>
    Stuck,

    /// <summary>Move the record to <see cref="FailoverDecision.TargetNodeId"/>.</summary>
    Switch,

    /// <summary>A switch is due, but failover is manual: the operator is told, nothing moves.</summary>
    Recommend,

    /// <summary>A switch was attempted and the DNS provider refused it. The record is unchanged.</summary>
    Failed
}

public sealed record FailoverDecision(FailoverAction Action, string Message, string? ActiveNodeId = null, string? TargetNodeId = null);

/// <summary>
/// Whether the record should move, and where. Pure: the fleet, its health and the clock come in,
/// a decision comes out, so the whole judgement can be tested without a panel or a network.
///
/// It is deliberately reluctant. A switch reconnects every client, and moving traffic onto a node
/// that is no better than the one it left is worse than staying put - so the active node has to
/// be failing for the whole grace period, a switch has to be outside the cooldown of the last one,
/// and the target has to be healthy right now. It never switches back on its own either: a node
/// that recovers is a standby like any other, and flapping between two half-working nodes is
/// the failure mode this is built to avoid.
/// </summary>
public static class FailoverPolicy
{
    public static FailoverDecision Decide(
        string? activeNodeId,
        DateTimeOffset? activeSince,
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyDictionary<string, NodeAssessment> health,
        long fleetRevision,
        bool requiresCurrentSchema,
        DateTimeOffset now,
        FailoverOptions options)
    {
        if (activeNodeId is null)
        {
            return new(FailoverAction.None, "No node is active yet. Failover protects the active node, so pick one first.");
        }

        var active = nodes.FirstOrDefault(node => node.Id == activeNodeId);
        if (active is null || !health.TryGetValue(active.Id, out var state))
        {
            return new(FailoverAction.None, "The active node is no longer enrolled.", activeNodeId);
        }

        if (!state.IsFailing)
        {
            return new(FailoverAction.None, $"{active.Name} is {state.State}.", active.Id);
        }

        var failingSince = state.FailingSince ?? active.EnrolledAt;
        var failingFor = now - failingSince;
        if (failingFor < options.Grace)
        {
            return new(
                FailoverAction.Wait,
                $"{active.Name} is {state.State} ({Describe(failingFor)}). Switching after {Describe(options.Grace)} unless it recovers.",
                active.Id);
        }

        var candidate = nodes
            .Where(node => node.Id != active.Id && IsEligible(node, health, requiresCurrentSchema))
            // A node that has not caught up with the fleet revision does not know the newest
            // clients yet. It beats no node, but not a node that does.
            .OrderByDescending(node => node.AppliedRevision == fleetRevision)
            // Seen from where clients are beats the node's word for itself.
            .ThenByDescending(node => health[node.Id].ProbeVerified)
            .ThenBy(node => node.FailoverPriority)
            .ThenBy(node => node.EnrolledAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            return new(
                FailoverAction.Stuck,
                $"{active.Name} has been {state.State} for {Describe(failingFor)} and no healthy node can take over.",
                active.Id);
        }

        if (activeSince is { } since && now - since < options.Cooldown)
        {
            return new(
                FailoverAction.Hold,
                $"{active.Name} has been {state.State} for {Describe(failingFor)}, but the last switch was {Describe(now - since)} ago. "
                + $"Holding until {Describe(options.Cooldown)} have passed.",
                active.Id,
                candidate.Id);
        }

        return new(
            FailoverAction.Switch,
            $"{active.Name} has been {state.State} for {Describe(failingFor)}: {state.Reason} Moving clients to {candidate.Name}.",
            active.Id,
            candidate.Id);
    }

    /// <summary>
    /// What a node needs before traffic can be sent to it without anyone looking. The manual
    /// button is more forgiving on purpose: an operator may know something the panel does not.
    /// </summary>
    public static bool IsEligible(NodeRecord node, IReadOnlyDictionary<string, NodeAssessment> health, bool requiresCurrentSchema)
        => !node.Revoked
            && node.AutoFailover
            && !string.IsNullOrWhiteSpace(node.PublicIp)
            && health.TryGetValue(node.Id, out var state)
            && state.State == NodeHealthStates.Healthy
            // A node a schema behind serves the downgraded wire format, which clients on the 3.x
            // profile cannot complete a handshake with. It looks healthy and carries nobody.
            && (!requiresCurrentSchema || node.BundleSchemaVersion >= DesiredStateBundle.CurrentSchemaVersion);

    internal static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 2)
        {
            return $"{((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
        }

        return span.TotalHours < 2
            ? $"{((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min"
            : $"{((int)span.TotalHours).ToString(CultureInfo.InvariantCulture)} h";
    }
}
