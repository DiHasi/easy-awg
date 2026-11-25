using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Current diagnostic checks of all discovered addresses. They do not select a DNS target.</summary>
public sealed record NodeAddressesResponse(string NodeId, int Port, AddressStatusResponse[] Ingress, AddressStatusResponse[] Egress);

/// <summary>Reachable if any current probe got traffic through, unreachable if all usable current
/// checks failed, and unknown without usable evidence. A failed entry cannot condemn an exit.</summary>
public sealed record AddressStatusResponse(string Address, string Outcome, DateTimeOffset? CheckedAt, AddressProbeResponse[] Probes);

/// <summary>The evidence behind one address status, including stale checks and probe errors.</summary>
public sealed record AddressProbeResponse(string ProbeId, string ProbeName, string IngressAddress,
    string Outcome, DateTimeOffset CheckedAt, bool Fresh, bool? Handshake, int? LatencyMs, string? Detail, string? ObservedAddress);

public sealed class AddressCheckService(
    ProbeAddressRepository checks, ProbeRepository probes, ProbePathRepository paths, ControlOptions options,
    FleetService fleet, Notifier notifier, EventLog events, NodeRepository nodes, PanelContext panel,
    ILogger<AddressCheckService> logger) : BackgroundService
{
    private readonly Dictionary<(string Node, string Direction, string Address), string> _states = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A burst of exit failures may send many alerts. Its delivery must never hold the
        // failover monitor's gate or postpone the next decision about the fleet's primary path.
        using var timer = new PeriodicTimer(options.Failover.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (panel.IsActive && !RotationSettling.IsSettling(fleet.Current.IdentityRotatedAt, now, options.Failover))
                    await ReportTransitionsAsync(nodes.List(), now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Address check notifications failed. Retrying next round.");
            }
        }
    }

    public NodeAddressesResponse Describe(NodeRecord node, DateTimeOffset now)
    {
        var names = probes.List().Where(probe => !probe.Revoked).ToDictionary(probe => probe.Id, probe => probe.Name);
        var rows = checks.List().Where(row => row.NodeId == node.Id).ToArray();
        var entries = NodeIngress.Addresses(node);
        var exits = paths.List().Where(path => path.NodeId == node.Id)
            .ToDictionary(path => (path.ProbeId, path.Address));
        var exitChecksEnabled = (options.Failover.ProbeEgressCheckUrls ?? FailoverOptions.DefaultEgressCheckUrls).Count > 0;
        AddressStatusResponse Status(string address, string direction)
        {
            var evidence = rows.Where(row => row.Direction == direction && row.Address == address && names.ContainsKey(row.ProbeId))
                .Select(row => new AddressProbeResponse(row.ProbeId, names[row.ProbeId], row.IngressAddress,
                    row.Outcome, row.CheckedAt,
                    !node.Revoked && now - row.CheckedAt <= options.Failover.ProbeStaleAfter
                        && entries.Contains(row.IngressAddress, StringComparer.Ordinal)
                        // Fresh traffic is evidence even when management is unreachable: nodes
                        // keep serving their cached bundle, including these peers and pins.
                        && (direction != "egress" || exitChecksEnabled
                            && exits.TryGetValue((row.ProbeId, address), out var path) && node.AppliedRevision >= path.Revision),
                    row.Handshake, row.LatencyMs, row.Detail, row.ObservedAddress)).ToArray();
            var usable = evidence.Where(row => row.Fresh && row.Outcome != ProbeOutcomes.Error).ToArray();
            var outcome = usable.Any(row => row.Outcome == ProbeOutcomes.Reachable) ? ProbeOutcomes.Reachable
                : usable.Length > 0 ? ProbeOutcomes.Unreachable : "unknown";
            return new(address, outcome, evidence.Length > 0 ? evidence.Max(row => row.CheckedAt) : null, evidence);
        }
        return new(node.Id, fleet.Current.ListenPort,
            entries.Select(address => Status(address, "ingress")).ToArray(),
            ReportedEgress.Read(node.EgressReport).Select(row => row.Address).Distinct(StringComparer.Ordinal)
                .Select(address => Status(address, "egress")).ToArray());
    }

    public async Task ReportTransitionsAsync(IReadOnlyList<NodeRecord> nodes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = new HashSet<(string Node, string Direction, string Address)>();
        foreach (var node in nodes.Where(node => !node.Revoked))
        {
            var state = Describe(node, now);
            foreach (var (direction, addresses) in new[] { ("ingress", state.Ingress), ("egress", state.Egress) })
            foreach (var address in addresses)
            {
                var key = (node.Id, direction, address.Address);
                known.Add(key);
                if (address.Outcome == "unknown") continue;
                var previous = _states.GetValueOrDefault(key);
                _states[key] = address.Outcome;
                if (previous == address.Outcome || previous is null && address.Outcome == ProbeOutcomes.Reachable) continue;
                var kind = $"address.{direction}.{(address.Outcome == ProbeOutcomes.Reachable ? "recovered" : "unreachable")}";
                var message = $"{node.Name}: {direction} address {address.Address} "
                    + (address.Outcome == ProbeOutcomes.Reachable ? "passes traffic through the tunnel again."
                        : "did not pass its tunnel traffic check from any probe with a current result.");
                events.Record(kind, message, nodeId: node.Id);
                await notifier.SendAsync(new(kind, $"{node.Name}: {direction} {address.Outcome}", message, node.Id, now), cancellationToken);
            }
        }
        foreach (var key in _states.Keys.Where(key => !known.Contains(key)).ToArray()) _states.Remove(key);
    }
}
