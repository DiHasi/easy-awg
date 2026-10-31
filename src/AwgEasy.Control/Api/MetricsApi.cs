using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// The fleet as a scraper reads it: Prometheus text exposition, one GET, no session.
///
/// It exists because the panel is the only place that knows some of these facts - that a node is
/// blocked rather than down, that it is three revisions behind, that no probe has reported for ten
/// minutes - and an operator should not have to keep a browser tab open to find out. The numbers
/// are the same ones the pages draw, read through the same evaluator, so the dashboard and the
/// panel can never disagree about whether a node is healthy.
///
/// Three deliberate choices:
///
/// Off until <c>AWG_METRICS_TOKEN</c> is set. This is the one route that answers without a
/// session, and the health and whereabouts of a fleet of VPN nodes is not something to start
/// publishing because a panel was upgraded.
///
/// Nothing about a person. No client names, no public keys, no per-peer series - a peer label
/// would put "who" into a metrics store that is backed up, federated and shared far more casually
/// than this database, and the client list is the panel's own bookkeeping about people. Traffic is
/// exposed per node, which is the operational question, and the Traffic page keeps the other one.
/// Node addresses are left out for the same reason: a fleet's addresses are what a blocker wants.
///
/// Written by hand rather than through a metrics library, like the chart on the Traffic page: the
/// exposition format is a few lines of text, and a dependency that pulls in its own registry and
/// collectors is weight on a panel that exports thirty series.
/// </summary>
public static class MetricsApi
{
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static void MapMetrics(this RouteGroupBuilder api)
    {
        api.MapGet("/metrics", (
            HttpContext context,
            ControlOptions options,
            FleetService fleet,
            FleetRepository fleetRepository,
            NodeRepository nodes,
            ClientRepository clients,
            ProbeRepository probes,
            FailoverMonitor monitor,
            PeerPresenceService presence) =>
        {
            if (!options.Metrics.IsEnabled)
            {
                return Results.NotFound(new ApiError(
                    "metrics_disabled",
                    "The metrics endpoint is off. Set AWG_METRICS_TOKEN on the panel to turn it on."));
            }

            if (!IsAuthorized(context, options.Metrics.Token!))
            {
                return Results.Json(
                    new ApiError("metrics_unauthorized", "A bearer token matching AWG_METRICS_TOKEN is required."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Text(Render(fleet, fleetRepository, nodes, clients, probes, monitor, presence), ContentType);
        }).AllowAnonymous();
    }

    /// <summary>
    /// A scrape credential is compared in fixed time like any other secret: this route is reachable
    /// without a session, so the comparison itself must not leak the token a byte at a time.
    /// </summary>
    private static bool IsAuthorized(HttpContext context, string token)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";

        var presented = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : header.Trim();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(token));
    }

    private static string Render(
        FleetService fleet,
        FleetRepository fleetRepository,
        NodeRepository nodes,
        ClientRepository clients,
        ProbeRepository probes,
        FailoverMonitor monitor,
        PeerPresenceService presence)
    {
        var now = DateTimeOffset.UtcNow;
        var current = fleet.Current;
        var fleetNodes = nodes.List();
        var health = monitor.Assess(fleetNodes, now);
        var traffic = nodes.AggregateByNode();
        var carrying = presence.Assess(fleetNodes, now);
        var peers = clients.List();
        var observations = probes.Observations(includeRevoked: true).ToLookup(observation => observation.ProbeId, StringComparer.Ordinal);
        var activeNodeId = fleetRepository.FindActiveNode().NodeId;
        var mode = fleetRepository.FindFailoverMode();

        var text = new StringBuilder();

        Family(text, "awg_up", "gauge", "Always 1 while the control plane answers.");
        Sample(text, "awg_up", 1);

        Family(text, "awg_fleet_revision", "gauge", "The configuration revision nodes are expected to converge to.");
        Sample(text, "awg_fleet_revision", current.Revision);

        Family(text, "awg_fleet_generation", "gauge", "Generation of the shared AmneziaWG identity.");
        Sample(text, "awg_fleet_generation", current.Generation);

        Family(text, "awg_fleet_peers", "gauge", "Configs the panel holds, by whether they are enabled.");
        Sample(text, "awg_fleet_peers", peers.Count(peer => peer.Enabled), ("state", "enabled"));
        Sample(text, "awg_fleet_peers", peers.Count(peer => !peer.Enabled), ("state", "disabled"));

        Family(text, "awg_fleet_nodes", "gauge", "Nodes enrolled in the fleet, by whether they still receive configuration.");
        Sample(text, "awg_fleet_nodes", fleetNodes.Count(node => !node.Revoked), ("state", "active"));
        Sample(text, "awg_fleet_nodes", fleetNodes.Count(node => node.Revoked), ("state", "revoked"));

        Family(text, "awg_failover_armed", "gauge", "1 when the panel may move the DNS record on its own.");
        Sample(text, "awg_failover_armed", mode == FailoverModes.Automatic ? 1 : 0);

        Family(text, "awg_failover_round_timestamp_seconds", "gauge", "When the fleet was last evaluated. 0 before the first round.");
        Sample(text, "awg_failover_round_timestamp_seconds", Seconds(monitor.LastRound?.At));

        // One series per state with a 1 on the current one, which is how a state set is exposed:
        // the dashboard can then alert on awg_node_health{state="blocked"} without having to know
        // what the other states are called.
        Family(text, "awg_node_health", "gauge", "1 on the state the panel currently judges a node to be in.");
        foreach (var node in fleetNodes)
        {
            var state = health[node.Id].State;
            foreach (var candidate in States)
            {
                Sample(text, "awg_node_health", state == candidate ? 1 : 0, ("node", node.Name), ("node_id", node.Id), ("state", candidate));
            }
        }

        Gauge(text, "awg_node_active", "1 on the node the failover record points at.", fleetNodes, node => node.Id == activeNodeId ? 1 : 0);
        Gauge(text, "awg_node_interface_up", "1 when the agent last reported its tunnel up.", fleetNodes, node => node.InterfaceUp ? 1 : 0);
        Gauge(text, "awg_node_revoked", "1 when the node no longer receives configuration.", fleetNodes, node => node.Revoked ? 1 : 0);
        Gauge(text, "awg_node_applied_revision", "The revision the node reports having applied.", fleetNodes, node => node.AppliedRevision);
        Gauge(text, "awg_node_revision_lag", "How many revisions behind the fleet this node is.", fleetNodes, node => current.Revision - node.AppliedRevision);
        Gauge(text, "awg_node_bundle_schema_version", "Highest bundle schema the agent reports understanding.", fleetNodes, node => node.BundleSchemaVersion);
        Gauge(text, "awg_node_report_timestamp_seconds", "When the agent last reported. 0 before its first report.", fleetNodes, node => Seconds(node.LastSeenAt));
        Gauge(text, "awg_node_probes_reachable", "Probes that got traffic through to this node in their latest round.", fleetNodes, node => health[node.Id].ProbesReachable);
        Gauge(text, "awg_node_probes_reporting", "Probes whose latest result about this node still counts.", fleetNodes, node => health[node.Id].ProbesReporting);
        // Three peer counts that mean three different things, which is why the help strings have
        // to be exact: this one is every peer the node is configured with, which in a fleet is
        // nearly the whole client list - every node carries every peer, probes included.
        Gauge(text, "awg_node_peers", "Peers configured on the node, as it last reported them.", fleetNodes, node => traffic.GetValueOrDefault(node.Id).Peers);

        // The two an operator draining a node alerts on. Counts of people, never a label naming
        // one: a per-peer series would put "who" into a store that is federated and backed up far
        // more casually than this database.
        Gauge(text, "awg_node_peers_online", "Peers handshaking through this node right now.", fleetNodes, node => carrying.For(node.Id).Online);
        Gauge(text, "awg_node_peers_idle", "Peers whose last known node is this one and which are not handshaking now.", fleetNodes, node => carrying.For(node.Id).Idle);

        // Counters as the node reports them, so a reboot shows up as the reset it is - which is
        // the one reader that knows what to do with that.
        Family(text, "awg_node_received_bytes_total", "counter", "Bytes this node has received from its peers since it last started counting.");
        foreach (var node in fleetNodes)
        {
            Sample(text, "awg_node_received_bytes_total", traffic.GetValueOrDefault(node.Id).Rx, ("node", node.Name), ("node_id", node.Id));
        }

        Family(text, "awg_node_transmitted_bytes_total", "counter", "Bytes this node has sent to its peers since it last started counting.");
        foreach (var node in fleetNodes)
        {
            Sample(text, "awg_node_transmitted_bytes_total", traffic.GetValueOrDefault(node.Id).Tx, ("node", node.Name), ("node_id", node.Id));
        }

        var fleetProbes = probes.List();

        Family(text, "awg_probe_report_timestamp_seconds", "gauge", "When each probe last reported a round. 0 before its first.");
        foreach (var probe in fleetProbes)
        {
            Sample(text, "awg_probe_report_timestamp_seconds", Seconds(probe.LastSeenAt), ("probe", probe.Name), ("probe_id", probe.Id));
        }

        Family(text, "awg_probe_revoked", "gauge", "1 when a probe's results no longer count.");
        foreach (var probe in fleetProbes)
        {
            Sample(text, "awg_probe_revoked", probe.Revoked ? 1 : 0, ("probe", probe.Name), ("probe_id", probe.Id));
        }

        // The latest round per vantage point, which is what says where a block is. Handshake is
        // kept apart from reachability on purpose: the two together are what separate a blocked
        // address from DPI that lets the handshake through and drops everything after it.
        var names = fleetNodes.ToDictionary(node => node.Id, node => node.Name, StringComparer.Ordinal);

        Family(text, "awg_probe_reachable", "gauge", "1 when a probe's latest round got traffic through to a node.");
        ProbeSamples(text, "awg_probe_reachable", fleetProbes, observations, names,
            observation => observation.Outcome == ProbeOutcomes.Reachable ? 1 : 0);

        Family(text, "awg_probe_handshake", "gauge", "1 when the handshake completed in that round, whatever followed it.");
        ProbeSamples(text, "awg_probe_handshake", fleetProbes, observations, names,
            observation => observation.Handshake == true ? 1 : 0);

        Family(text, "awg_probe_error", "gauge", "1 when the probe could not run its own check, which counts against nobody.");
        ProbeSamples(text, "awg_probe_error", fleetProbes, observations, names,
            observation => observation.Outcome == ProbeOutcomes.Error ? 1 : 0);

        Family(text, "awg_probe_latency_milliseconds", "gauge", "Round trip through the tunnel, or time to the handshake when there was none. Coarse.");
        ProbeSamples(text, "awg_probe_latency_milliseconds", fleetProbes, observations, names,
            observation => observation.LatencyMs ?? 0);

        Family(text, "awg_probe_check_timestamp_seconds", "gauge", "When that probe last checked that node.");
        ProbeSamples(text, "awg_probe_check_timestamp_seconds", fleetProbes, observations, names,
            observation => Seconds(observation.CheckedAt));

        return text.ToString();
    }

    private static readonly string[] States =
    [
        NodeHealthStates.Healthy,
        NodeHealthStates.Blocked,
        NodeHealthStates.Down,
        NodeHealthStates.Silent,
        NodeHealthStates.Unknown,
        NodeHealthStates.Revoked
    ];

    private static void Gauge(
        StringBuilder text,
        string name,
        string help,
        IReadOnlyList<NodeRecord> fleetNodes,
        Func<NodeRecord, double> value)
    {
        Family(text, name, "gauge", help);
        foreach (var node in fleetNodes)
        {
            Sample(text, name, value(node), ("node", node.Name), ("node_id", node.Id));
        }
    }

    private static void ProbeSamples(
        StringBuilder text,
        string name,
        IReadOnlyList<ProbeRecord> fleetProbes,
        ILookup<string, ProbeObservation> observations,
        IReadOnlyDictionary<string, string> nodeNames,
        Func<ProbeObservation, double> value)
    {
        foreach (var probe in fleetProbes)
        {
            foreach (var observation in observations[probe.Id])
            {
                // A result about a node the panel has since forgotten is skipped rather than
                // labelled with a bare id nothing can resolve.
                if (!nodeNames.TryGetValue(observation.NodeId, out var nodeName))
                {
                    continue;
                }

                Sample(
                    text,
                    name,
                    value(observation),
                    ("probe", probe.Name),
                    ("probe_id", probe.Id),
                    ("node", nodeName),
                    ("node_id", observation.NodeId));
            }
        }
    }

    private static void Family(StringBuilder text, string name, string type, string help)
        => text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Sample(StringBuilder text, string name, double value, params (string Name, string Value)[] labels)
    {
        text.Append(name);
        if (labels.Length > 0)
        {
            text.Append('{');
            for (var index = 0; index < labels.Length; index++)
            {
                if (index > 0)
                {
                    text.Append(',');
                }

                text.Append(labels[index].Name).Append("=\"").Append(Escape(labels[index].Value)).Append('"');
            }

            text.Append('}');
        }

        text.Append(' ').Append(value.ToString("G17", CultureInfo.InvariantCulture)).Append('\n');
    }

    /// <summary>
    /// A node name is whatever the operator typed, and the exposition format has exactly three
    /// characters it cannot carry raw. Unescaped, a name with a quote in it would not merely look
    /// wrong - it would break the parse of every line after it.
    /// </summary>
    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Unix seconds, the only time unit the format has. Zero for "never": a timestamp series with
    /// a gap in it cannot be alerted on, and zero is unambiguously before everything.
    /// </summary>
    private static double Seconds(DateTimeOffset? at) => at?.ToUnixTimeMilliseconds() / 1000d ?? 0;
}
