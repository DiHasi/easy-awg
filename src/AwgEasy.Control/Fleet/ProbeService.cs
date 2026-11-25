using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Probes: clients of the fleet that exist to check whether a handshake with each node completes
/// from where the people using it are.
///
/// Each probe is backed by a client row of its own. That is what makes its result mean anything:
/// it is a peer on every node like any person's client, so a handshake that completes for the
/// probe would complete for them. Its address comes from the same allocator, because a probe
/// that happened to share an address with a person would steal their traffic on whichever node
/// saw it last.
/// </summary>
public sealed class ProbeService(
    ProbeRepository probes,
    ProbePathRepository paths,
    ProbeAddressRepository addressChecks,
    ClientRepository clients,
    NodeRepository nodes,
    AvailabilityRepository availability,
    FleetService fleet,
    IAwgKeyGenerator keys,
    ControlOptions options,
    EventLog events)
{
    public ProbeRecord? FindByAgentKey(string agentPublicKey) => probes.FindByAgentKey(agentPublicKey);

    public (ProbeRecord? Probe, ApiError Error) Create(string name, string? hostname, string agentPublicKey, string? agentVersion, DateTimeOffset now)
    {
        var current = fleet.Current;
        if (!AddressAllocator.TryAllocate(current.Subnet, clients.UsedAddresses(), out var address, out var allocationError))
        {
            return (null, allocationError);
        }

        var probeId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var privateKey = keys.GeneratePrivateKey();
        var client = new ClientRecord(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            // Unique by construction and never shown as a person's name: probe clients are
            // filtered out of the client list, and the bundle carries no names at all.
            $"probe:{probeId}",
            address,
            privateKey,
            keys.GeneratePublicKey(privateKey),
            keys.GeneratePresharedKey(),
            Enabled: true,
            Obfuscation: null,
            now,
            now);

        clients.Insert(client, ClientKinds.Probe);

        var probe = new ProbeRecord(probeId, name, hostname, agentPublicKey, agentVersion, client.Id, null, false, now);
        probes.Insert(probe);

        // Its key has to be on every node before its first handshake can mean anything.
        fleet.BumpRevision($"probe {name} enrolled");
        return (probe, ApiError.Empty);
    }

    public ProbeAssignment BuildAssignment(ProbeRecord probe)
    {
        var current = fleet.Current;
        var client = clients.FindProbeClient(probe.ClientId);
        var fleetNodes = nodes.List();
        var exitChecks = options.Failover.ProbeEgressCheckUrls ?? FailoverOptions.DefaultEgressCheckUrls;
        var exits = client is { Enabled: true } ? paths.Ensure(probe, current, exitChecks.Count > 0 ? fleetNodes : []) : [];
        // Ensure may have added peers. Configs use the unchanged identity, but assignments must
        // wait for the node to apply the revision containing the extra peer and its SNAT rule.
        current = fleet.Current;

        // Every node that could carry traffic: not revoked, and with an address to aim at. The
        // active one and the standbys alike - a standby nobody has checked is not a standby.
        var targets = client is null || !client.Enabled
            ? []
            : fleetNodes
                .Where(node => !node.Revoked && !string.IsNullOrWhiteSpace(node.PublicIp))
                .Select(node => new ProbeTarget(
                    node.Id,
                    node.PublicIp!,
                    current.ListenPort,
                    ClientConfigRenderer.RenderProbe(current, client, node.PublicIp!)))
                .ToArray();

        var additional = new List<ProbeTarget>();
        if (client is { Enabled: true })
        {
            var observations = addressChecks.List();
            foreach (var node in fleetNodes.Where(node => !node.Revoked))
            {
                var entries = NodeIngress.Addresses(node);
                foreach (var entry in entries.Where(entry => entry != node.PublicIp))
                    additional.Add(new(node.Id, entry, current.ListenPort, ClientConfigRenderer.RenderProbe(current, client, entry)));

                // Prefer an entry the probe has actually reached. An entry that cannot handshake
                // makes the exit unknown, never failed: it did not get as far as checking it.
                var ingress = observations.Where(row => row.ProbeId == probe.Id && row.NodeId == node.Id
                        && row.Direction == "ingress" && row.Outcome == ProbeOutcomes.Reachable
                        && DateTimeOffset.UtcNow - row.CheckedAt <= options.Failover.ProbeStaleAfter
                        && entries.Contains(row.Address, StringComparer.Ordinal))
                    .OrderByDescending(row => row.CheckedAt).Select(row => row.Address).FirstOrDefault()
                    ?? entries.FirstOrDefault();
                if (ingress is null) continue;
                foreach (var path in exits.Where(path => path.NodeId == node.Id && path.Revision <= node.AppliedRevision))
                {
                    if (clients.FindProbeClient(path.ClientId) is not { Enabled: true } peer) continue;
                    var observed = ReportedEgress.Read(node.EgressReport).First(address => address.Address == path.Address).Observed;
                    var expected = observed ?? (PublicIpAddress.TryParse(path.Address, out _) ? path.Address : null);
                    additional.Add(new(node.Id, ingress, current.ListenPort,
                        ClientConfigRenderer.RenderProbe(current, peer, ingress), path.Address, expected));
                }
            }
        }

        return new ProbeAssignment(
            probe.Id,
            (int)options.Failover.ProbeInterval.TotalSeconds,
            (int)options.Failover.ProbeHandshakeTimeout.TotalSeconds,
            targets,
            [.. options.Failover.ProbeCheckUrls],
            (int)options.Failover.ProbeTrafficTimeout.TotalSeconds,
            [.. additional],
            [.. options.Failover.ProbeEgressCheckUrls ?? FailoverOptions.DefaultEgressCheckUrls]);
    }

    /// <summary>
    /// Stops trusting the probe and takes its key off every node. Its results stop counting at
    /// once; they are kept so the operator can still see what it last said.
    /// </summary>
    public bool Revoke(string id, string? actor)
    {
        var probe = probes.Find(id);
        if (probe is null || !probes.SetRevoked(id, true))
        {
            return false;
        }

        if (clients.SetEnabled(probe.ClientId, false, DateTimeOffset.UtcNow))
        {
            fleet.BumpRevision($"probe {probe.Name} revoked");
        }

        if (paths.Remove(probeId: id)) fleet.BumpRevision($"probe {probe.Name} egress peers revoked");

        events.Record("probe.revoked", $"Probe '{probe.Name}' revoked.", actor: actor);
        return true;
    }

    public bool Delete(string id, string? actor)
    {
        var probe = probes.Find(id);
        if (probe is null || !probes.Delete(id))
        {
            return false;
        }

        availability.ForgetProbe(id);
        if (paths.Remove(probeId: id)) fleet.BumpRevision($"probe {probe.Name} egress peers deleted");

        var client = clients.FindProbeClient(probe.ClientId);
        if (client is not null && clients.Delete(client.Id))
        {
            nodes.ForgetPeer(client.PublicKey);
            fleet.BumpRevision($"probe {probe.Name} deleted");
        }

        events.Record("probe.deleted", $"Probe '{probe.Name}' removed.", actor: actor);
        return true;
    }

    public ProbeResponse[] Describe()
    {
        var observations = probes.Observations(includeRevoked: true).ToLookup(observation => observation.ProbeId, StringComparer.Ordinal);
        return probes.List()
            .Select(probe => new ProbeResponse(
                probe.Id,
                probe.Name,
                probe.Hostname,
                probe.AgentVersion,
                probe.LastSeenAt,
                probe.Revoked,
                probe.EnrolledAt,
                [.. observations[probe.Id].Select(observation => new ProbeResultResponse(
                    observation.NodeId,
                    observation.Address,
                    observation.Outcome,
                    observation.CheckedAt,
                    observation.LastReachableAt,
                    observation.LatencyMs,
                    observation.Detail,
                    observation.Handshake))]))
            .ToArray();
    }
}
