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
    ClientRepository clients,
    NodeRepository nodes,
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

        // Every node that could carry traffic: not revoked, and with an address to aim at. The
        // active one and the standbys alike - a standby nobody has checked is not a standby.
        var targets = client is null || !client.Enabled
            ? []
            : nodes.List()
                .Where(node => !node.Revoked && !string.IsNullOrWhiteSpace(node.PublicIp))
                .Select(node => new ProbeTarget(
                    node.Id,
                    node.PublicIp!,
                    current.ListenPort,
                    ClientConfigRenderer.RenderProbe(current, client, node.PublicIp!)))
                .ToArray();

        return new ProbeAssignment(
            probe.Id,
            (int)options.Failover.ProbeInterval.TotalSeconds,
            (int)options.Failover.ProbeHandshakeTimeout.TotalSeconds,
            targets,
            [.. options.Failover.ProbeCheckUrls],
            (int)options.Failover.ProbeTrafficTimeout.TotalSeconds);
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
