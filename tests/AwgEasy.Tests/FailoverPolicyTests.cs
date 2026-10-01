using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

internal static class Fleet
{
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    public static readonly FailoverOptions Options = new(
        CheckInterval: TimeSpan.FromSeconds(30),
        Grace: TimeSpan.FromMinutes(2),
        Cooldown: TimeSpan.FromMinutes(15),
        AgentStaleAfter: TimeSpan.FromSeconds(90),
        ProbeStaleAfter: TimeSpan.FromMinutes(5),
        ProbeInterval: TimeSpan.FromSeconds(60),
        ProbeHandshakeTimeout: TimeSpan.FromSeconds(15),
        ProbeCheckUrls: FailoverOptions.DefaultProbeCheckUrls,
        ProbeTrafficTimeout: TimeSpan.FromSeconds(8));

    public static NodeRecord Node(
        string id,
        bool interfaceUp = true,
        TimeSpan? seenAgo = null,
        TimeSpan? upAgo = null,
        string? publicIp = null,
        long appliedRevision = 10,
        int priority = NodeRecord.DefaultFailoverPriority,
        bool autoFailover = true,
        bool revoked = false,
        int schema = DesiredStateBundle.CurrentSchemaVersion,
        int enrolledOrder = 0)
    {
        var seen = Now - (seenAgo ?? TimeSpan.FromSeconds(10));
        return new NodeRecord(
            id,
            id,
            Hostname: null,
            EndpointHost: null,
            AgentPublicKey: "key-" + id,
            AgentVersion: "1.0",
            Status: NodeStatuses.Healthy,
            AppliedRevision: appliedRevision,
            InterfaceUp: interfaceUp,
            Backend: "Kernel module",
            BundleSchemaVersion: schema,
            EgressInterface: null,
            Mtu: null,
            PublicIp: publicIp ?? $"203.0.113.{Math.Abs(id.GetHashCode()) % 200 + 1}",
            LastSeenAt: seen,
            LastError: null,
            Revoked: revoked,
            EnrolledAt: Now.AddDays(-30).AddMinutes(enrolledOrder),
            LastUpAt: interfaceUp ? seen : Now - (upAgo ?? TimeSpan.FromHours(1)),
            FailoverPriority: priority,
            AutoFailover: autoFailover);
    }

    public static ProbeObservation Probe(
        NodeRecord node,
        string outcome,
        string probeId = "probe-1",
        TimeSpan? checkedAgo = null,
        TimeSpan? failingFor = null,
        string? address = null,
        bool? handshake = null)
        => new(
            probeId,
            node.Id,
            address ?? node.PublicIp!,
            outcome,
            Now - (checkedAgo ?? TimeSpan.FromSeconds(20)),
            outcome == ProbeOutcomes.Reachable ? Now - (checkedAgo ?? TimeSpan.FromSeconds(20)) : null,
            outcome == ProbeOutcomes.Unreachable ? Now - (failingFor ?? TimeSpan.FromMinutes(10)) : null,
            null,
            null,
            handshake ?? outcome == ProbeOutcomes.Reachable);
}

/// <summary>
/// The two kinds of evidence and what they add up to. The case that matters most is the one a
/// node can never report about itself: running fine, and unreachable from where clients are.
/// </summary>
public class NodeHealthEvaluatorTests
{
    private static NodeAssessment Assess(NodeRecord node, params ProbeObservation[] observations)
        => NodeHealthEvaluator.Assess(node, observations, Fleet.Now, Fleet.Options);

    [Fact]
    public void Trusts_the_agent_when_no_probe_is_watching()
    {
        var health = Assess(Fleet.Node("a"));

        Assert.Equal(NodeHealthStates.Healthy, health.State);
        Assert.Equal(NodeHealthEvaluator.AgentSource, health.Source);
        // It says what it cannot see rather than claiming more than it knows.
        Assert.Contains("unnoticed", health.Reason);
    }

    [Fact]
    public void Reads_a_tunnel_the_agent_reports_down_as_down()
    {
        var health = Assess(Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(7)));

        Assert.Equal(NodeHealthStates.Down, health.State);
        Assert.Equal(Fleet.Now - TimeSpan.FromMinutes(7), health.FailingSince);
    }

    [Fact]
    public void Reads_an_agent_that_stopped_reporting_as_silent()
    {
        var health = Assess(Fleet.Node("a", seenAgo: TimeSpan.FromMinutes(10)));

        Assert.Equal(NodeHealthStates.Silent, health.State);
        Assert.True(health.IsFailing);
    }

    [Fact]
    public void Is_blocked_when_the_agent_is_fine_and_no_probe_gets_a_handshake()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable), Fleet.Probe(node, ProbeOutcomes.Unreachable, probeId: "probe-2"));

        Assert.Equal(NodeHealthStates.Blocked, health.State);
        Assert.Equal(NodeHealthEvaluator.ProbeSource, health.Source);
        Assert.Equal(2, health.ProbesReporting);
    }

    // The block seen in practice: the handshake gets through, everything after it is dropped. It
    // must read as blocked - a handshake alone used to read as healthy - and say which kind it is.
    [Fact]
    public void Is_blocked_when_the_handshake_completes_and_no_traffic_follows()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable, handshake: true));

        Assert.Equal(NodeHealthStates.Blocked, health.State);
        Assert.Contains("no traffic came back", health.Reason);
    }

    [Fact]
    public void Says_the_address_is_blocked_when_not_even_the_handshake_completes()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable, handshake: false));

        Assert.Contains("none of 1 probe(s) completed a handshake", health.Reason);
    }

    [Fact]
    public void Is_down_when_neither_the_agent_nor_a_probe_reaches_it()
    {
        var node = Fleet.Node("a", seenAgo: TimeSpan.FromMinutes(10));

        Assert.Equal(NodeHealthStates.Down, Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable)).State);
    }

    // One vantage point getting through is enough: a single blocked ISP is not the fleet's problem
    // to solve by moving everybody.
    [Fact]
    public void Is_healthy_when_any_probe_completes_a_handshake()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable), Fleet.Probe(node, ProbeOutcomes.Reachable, probeId: "probe-2"));

        Assert.Equal(NodeHealthStates.Healthy, health.State);
        Assert.Equal(1, health.ProbesReachable);
    }

    // A probe whose own tooling broke says nothing about the node. If it counted, a broken probe
    // could move a whole fleet's traffic.
    [Fact]
    public void Ignores_results_where_the_probe_itself_failed()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Error));

        Assert.Equal(NodeHealthStates.Healthy, health.State);
        Assert.Equal(NodeHealthEvaluator.AgentSource, health.Source);
    }

    [Fact]
    public void Ignores_results_that_are_too_old_to_mean_anything()
    {
        var node = Fleet.Node("a");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable, checkedAgo: TimeSpan.FromMinutes(30)));

        Assert.Equal(NodeHealthStates.Healthy, health.State);
        Assert.Equal(NodeHealthEvaluator.AgentSource, health.Source);
    }

    // The node moved; reaching - or failing to reach - its old address says nothing about the new one.
    [Fact]
    public void Ignores_results_about_an_address_the_node_no_longer_has()
    {
        var node = Fleet.Node("a", publicIp: "203.0.113.50");

        var health = Assess(node, Fleet.Probe(node, ProbeOutcomes.Unreachable, address: "198.51.100.50"));

        Assert.Equal(NodeHealthEvaluator.AgentSource, health.Source);
    }

    // The node is failing only once every probe fails, so it began failing when the last of them
    // did - not when the first one lost it.
    [Fact]
    public void Counts_the_failure_from_when_the_last_probe_lost_the_node()
    {
        var node = Fleet.Node("a");

        var health = Assess(
            node,
            Fleet.Probe(node, ProbeOutcomes.Unreachable, failingFor: TimeSpan.FromMinutes(20)),
            Fleet.Probe(node, ProbeOutcomes.Unreachable, probeId: "probe-2", failingFor: TimeSpan.FromMinutes(3)));

        Assert.Equal(Fleet.Now - TimeSpan.FromMinutes(3), health.FailingSince);
    }
}

/// <summary>
/// When the record moves and where to. Reluctance is the feature: every switch reconnects every
/// client, so the policy only moves traffic that is certainly stuck to a node that is certainly fine.
/// </summary>
public class FailoverPolicyTests
{
    private static FailoverDecision Decide(
        string? active,
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyList<ProbeObservation>? observations = null,
        TimeSpan? activeFor = null,
        bool requiresCurrentSchema = false)
    {
        var health = NodeHealthEvaluator.AssessAll(nodes, observations ?? [], Fleet.Now, Fleet.Options);
        return FailoverPolicy.Decide(
            active,
            Fleet.Now - (activeFor ?? TimeSpan.FromHours(3)),
            nodes,
            health,
            fleetRevision: 10,
            requiresCurrentSchema,
            Fleet.Now,
            Fleet.Options);
    }

    [Fact]
    public void Does_nothing_while_the_active_node_is_healthy()
        => Assert.Equal(FailoverAction.None, Decide("a", [Fleet.Node("a"), Fleet.Node("b")]).Action);

    [Fact]
    public void Does_nothing_without_an_active_node()
        => Assert.Equal(FailoverAction.None, Decide(null, [Fleet.Node("a", interfaceUp: false)]).Action);

    [Fact]
    public void Waits_out_a_failure_shorter_than_the_grace_period()
    {
        var decision = Decide("a", [Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromSeconds(40)), Fleet.Node("b")]);

        Assert.Equal(FailoverAction.Wait, decision.Action);
    }

    [Fact]
    public void Switches_once_the_active_node_has_failed_for_the_whole_grace_period()
    {
        var decision = Decide("a", [Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)), Fleet.Node("b")]);

        Assert.Equal(FailoverAction.Switch, decision.Action);
        Assert.Equal("b", decision.TargetNodeId);
    }

    // The case a node cannot see about itself, decided by probes alone.
    [Fact]
    public void Switches_away_from_a_blocked_node()
    {
        var a = Fleet.Node("a");
        var b = Fleet.Node("b");

        var decision = Decide(
            "a",
            [a, b],
            [Fleet.Probe(a, ProbeOutcomes.Unreachable, failingFor: TimeSpan.FromMinutes(5)), Fleet.Probe(b, ProbeOutcomes.Reachable)]);

        Assert.Equal(FailoverAction.Switch, decision.Action);
        Assert.Equal("b", decision.TargetNodeId);
    }

    // Two half-working nodes would otherwise pass the traffic back and forth every grace period.
    [Fact]
    public void Holds_within_the_cooldown_of_the_last_switch()
    {
        var decision = Decide(
            "a",
            [Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)), Fleet.Node("b")],
            activeFor: TimeSpan.FromMinutes(4));

        Assert.Equal(FailoverAction.Hold, decision.Action);
        Assert.Equal("b", decision.TargetNodeId);
    }

    [Fact]
    public void Is_stuck_when_no_node_can_take_over()
    {
        var decision = Decide(
            "a",
            [Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)), Fleet.Node("b", seenAgo: TimeSpan.FromMinutes(10))]);

        Assert.Equal(FailoverAction.Stuck, decision.Action);
        Assert.Null(decision.TargetNodeId);
    }

    // A standby that probes cannot reach is not a standby. Moving clients onto it would move them
    // from one node they cannot reach to another.
    [Fact]
    public void Does_not_move_clients_to_a_node_that_is_blocked_too()
    {
        var a = Fleet.Node("a");
        var b = Fleet.Node("b");

        var decision = Decide(
            "a",
            [a, b],
            [Fleet.Probe(a, ProbeOutcomes.Unreachable, failingFor: TimeSpan.FromMinutes(5)), Fleet.Probe(b, ProbeOutcomes.Unreachable)]);

        Assert.Equal(FailoverAction.Stuck, decision.Action);
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("revoked")]
    [InlineData("no-address")]
    [InlineData("old-schema")]
    public void Never_picks_a_node_that_is_not_eligible(string why)
    {
        var standby = why switch
        {
            "excluded" => Fleet.Node("b", autoFailover: false),
            "revoked" => Fleet.Node("b", revoked: true),
            "no-address" => Fleet.Node("b") with { PublicIp = null },
            _ => Fleet.Node("b", schema: DesiredStateBundle.MinimumSupportedSchemaVersion)
        };

        var decision = Decide(
            "a",
            [Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)), standby],
            requiresCurrentSchema: true);

        Assert.Equal(FailoverAction.Stuck, decision.Action);
    }

    [Fact]
    public void Prefers_the_lowest_priority_number()
    {
        var decision = Decide(
            "a",
            [
                Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)),
                Fleet.Node("b", priority: 50, enrolledOrder: 1),
                Fleet.Node("c", priority: 10, enrolledOrder: 2)
            ]);

        Assert.Equal("c", decision.TargetNodeId);
    }

    // A node that has not applied the current revision does not have the newest clients yet.
    [Fact]
    public void Prefers_a_node_that_is_in_sync_over_priority()
    {
        var decision = Decide(
            "a",
            [
                Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5)),
                Fleet.Node("b", priority: 0, appliedRevision: 9),
                Fleet.Node("c", priority: 50)
            ]);

        Assert.Equal("c", decision.TargetNodeId);
    }

    [Fact]
    public void Prefers_a_node_probes_have_reached_over_one_only_its_agent_vouches_for()
    {
        var a = Fleet.Node("a", interfaceUp: false, upAgo: TimeSpan.FromMinutes(5));
        var b = Fleet.Node("b", priority: 0);
        var c = Fleet.Node("c", priority: 50);

        var decision = Decide("a", [a, b, c], [Fleet.Probe(c, ProbeOutcomes.Reachable)]);

        Assert.Equal("c", decision.TargetNodeId);
    }
}

public class ProbeCheckUrlOptionsTests
{
    [Fact]
    public void Checks_traffic_by_default()
        => Assert.NotEmpty(FailoverOptions.FromEnvironment(_ => null, (_, fallback) => fallback).ProbeCheckUrls);

    [Fact]
    public void Reads_a_comma_separated_list()
        => Assert.Equal(
            ["http://10.0.0.1/", "http://example.net/"],
            FailoverOptions.FromEnvironment(_ => "http://10.0.0.1/, http://example.net/", (_, fallback) => fallback).ProbeCheckUrls);

    // Off only when asked for by name: an empty list would quietly fall back to the handshake
    // alone, which is exactly the check the common block gets through.
    [Fact]
    public void Switches_the_traffic_check_off_only_when_told_to()
        => Assert.Empty(FailoverOptions.FromEnvironment(_ => "none", (_, fallback) => fallback).ProbeCheckUrls);
}
