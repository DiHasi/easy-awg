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
    /// <param name="identityRevision">
    /// The revision the fleet identity last changed at. A node below it is running the previous
    /// key pair and preshared keys, so it cannot serve a single config the panel has issued since -
    /// sending traffic there would be a switch to a node where every client fails.
    /// </param>
    public static FailoverDecision Decide(
        string? activeNodeId,
        DateTimeOffset? activeSince,
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyDictionary<string, NodeAssessment> health,
        long fleetRevision,
        long identityRevision,
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

        var others = nodes.Where(node => node.Id != active.Id).ToArray();
        var candidate = others
            .Where(node => IsEligible(node, health, requiresCurrentSchema, identityRevision))
            // A node that has not caught up with the fleet revision does not know the newest
            // clients yet. It beats no node, but not a node that does. Only a preference, unlike
            // the identity revision above - one peer missing is not every peer failing.
            .OrderByDescending(node => node.AppliedRevision == fleetRevision)
            // Seen from where clients are beats the node's word for itself.
            .ThenByDescending(node => health[node.Id].ProbeVerified)
            .ThenBy(node => node.FailoverPriority)
            .ThenBy(node => node.EnrolledAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            // A node held back only by the identity revision is a different message. It reads as
            // healthy on the Nodes page, so "no healthy node can take over" would send the
            // operator looking for a fault that is not there - what it needs is one more poll.
            // Zero is the revision every node is at or above, so asking with it answers "eligible
            // apart from the rotation".
            const long AnyIdentity = 0;
            var behind = others.Count(node => node.AppliedRevision < identityRevision
                && IsEligible(node, health, requiresCurrentSchema, AnyIdentity));

            return new(
                FailoverAction.Stuck,
                behind > 0
                    ? $"{active.Name} has been {state.State} for {Describe(failingFor)} and no node can take over: "
                      + $"{Count(behind)} healthy node(s) are still applying the rotated identity and would serve no current config yet."
                    : $"{active.Name} has been {state.State} for {Describe(failingFor)} and no healthy node can take over.",
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
    ///
    /// The identity revision is the exception to that - it is checked on the manual path too, in
    /// <see cref="DnsFailoverService.ActivateAsync"/>. The rest of these are judgements an
    /// operator may reasonably overrule; a node on the previous identity is not a judgement but
    /// an arithmetic fact about which key pair it is holding, and overruling it sends every
    /// client to a server that will refuse all of them.
    /// </summary>
    public static bool IsEligible(
        NodeRecord node,
        IReadOnlyDictionary<string, NodeAssessment> health,
        bool requiresCurrentSchema,
        long identityRevision)
        => !node.Revoked
            && node.AutoFailover
            && !string.IsNullOrWhiteSpace(node.PublicIp)
            && health.TryGetValue(node.Id, out var state)
            && state.State == NodeHealthStates.Healthy
            // Still on the identity before the last rotation: the old key pair, the old preshared
            // keys, the old wire format. It is up, it reports healthy, and not one config issued
            // since the rotation can complete a handshake with it.
            && node.AppliedRevision >= identityRevision
            // A node a schema behind serves the downgraded wire format, which clients on the 3.x
            // profile cannot complete a handshake with. It looks healthy and carries nobody.
            && (!requiresCurrentSchema || node.BundleSchemaVersion >= DesiredStateBundle.CurrentSchemaVersion);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

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
