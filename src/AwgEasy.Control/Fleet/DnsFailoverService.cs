using System.Net;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Manual failover: moves the one DNS record every client config points at from one node to
/// another.
///
/// This is the whole reason the fleet shares an identity. A client config pins the server public
/// key and names a host, never an address, so repointing that host at a different node changes
/// nothing the client can see - same key, same subnet, same obfuscation profile, new server. No
/// config is reissued and no node re-applies anything, which is why switching deliberately does
/// not touch the fleet revision.
///
/// Phase 2 replaces the operator's judgement here with a health probe, not this code: the probe
/// will call <see cref="ActivateAsync"/> exactly as the button does.
/// </summary>
public sealed class DnsFailoverService(
    FleetService fleet,
    FleetRepository fleetRepository,
    NodeRepository nodes,
    IDnsRecordUpdater updater,
    IHostAddressResolver resolver,
    ControlOptions options,
    EventLog events,
    ILogger<DnsFailoverService> logger)
{
    /// <summary>
    /// Defaults to the host clients already carry. Overridable because a deployment may want to
    /// move a dedicated record - say a CNAME target - rather than the name in the configs.
    /// </summary>
    public string RecordName => options.Dns.RecordName ?? fleet.Current.EndpointHost;

    public async Task<(DnsStatusResponse? Status, ApiError? Error)> ActivateAsync(
        string nodeId,
        string? actor,
        CancellationToken cancellationToken)
    {
        var node = nodes.Find(nodeId);
        if (node is null)
        {
            return (null, new ApiError("node_not_found", "Node was not found."));
        }

        if (node.Revoked)
        {
            // Pointing clients at a node the panel refuses to configure is a trap: it serves its
            // cached bundle now and drifts further from the fleet with every change made here.
            return (null, new ApiError(
                "node_revoked",
                "This node is revoked, so it no longer receives configuration. Restore it before sending clients to it."));
        }

        if (string.IsNullOrWhiteSpace(node.PublicIp))
        {
            return (null, new ApiError(
                "node_public_ip_unknown",
                "This node has not reported a public address yet. Its agent reports one with the next status update; "
                + "set AWG_PUBLIC_IP on the node if it cannot discover its own address."));
        }

        var target = new DnsRecordTarget(RecordName, node.PublicIp, options.Dns.Ttl);
        var result = await updater.PointAsync(target, cancellationToken);

        if (result.Outcome == DnsUpdateOutcome.Failed)
        {
            // The stored active node means "this is where the record points", so it must not move
            // when the record did not. Leaving it alone keeps the panel's claim true.
            logger.LogWarning("Refusing to mark {NodeName} active: the DNS record was not changed.", node.Name);
            return (null, result.Error ?? new ApiError("dns_update_failed", "The DNS record could not be updated."));
        }

        fleetRepository.SetActiveNode(node.Id, DateTimeOffset.UtcNow);

        var applied = result.Outcome == DnsUpdateOutcome.Applied;
        events.Record(
            "node.activated",
            applied
                ? $"Node '{node.Name}' is now active: {RecordName} points at {node.PublicIp} via {updater.ProviderName}."
                : $"Node '{node.Name}' marked active. {result.Detail}",
            actor: actor,
            nodeId: node.Id);

        logger.LogInformation(
            "Active node is now {NodeName} ({Address}); DNS {Outcome}.",
            node.Name,
            node.PublicIp,
            applied ? "updated" : "left to the operator");

        return (await DescribeAsync(cancellationToken), null);
    }

    /// <summary>Clears the record's target when the node it pointed at leaves the fleet.</summary>
    public void ForgetIfActive(string nodeId)
    {
        if (fleetRepository.FindActiveNode().NodeId == nodeId)
        {
            fleetRepository.SetActiveNode(null, DateTimeOffset.UtcNow);
        }
    }

    public string? ActiveNodeId => fleetRepository.FindActiveNode().NodeId;

    public async Task<DnsStatusResponse> DescribeAsync(CancellationToken cancellationToken)
    {
        var (activeNodeId, setAt) = fleetRepository.FindActiveNode();
        var active = activeNodeId is null ? null : nodes.Find(activeNodeId);
        var recordName = RecordName;

        // A second opinion, not proof: this host caches like any other, so a just-changed record
        // can still read stale here for the length of the TTL it was last served with.
        var resolved = await resolver.ResolveAsync(recordName, cancellationToken);

        var target = active?.PublicIp;
        return new DnsStatusResponse(
            updater.ProviderName,
            updater.IsConfigured,
            recordName,
            target is null ? "A" : new DnsRecordTarget(recordName, target, options.Dns.Ttl).RecordType,
            options.Dns.Ttl,
            active?.Id,
            active?.Name,
            target,
            resolved,
            target is not null && resolved.Contains(target, StringComparer.OrdinalIgnoreCase),
            setAt,
            Warn(recordName, target, resolved));
    }

    private static string? Warn(string recordName, string? target, string[] resolved)
    {
        if (IPAddress.TryParse(recordName, out _))
        {
            return "AWG_ENDPOINT_HOST is an address, not a name, so there is no record to move and client "
                + "configs are pinned to one node. Point it at a hostname you control and reissue configs.";
        }

        if (target is null)
        {
            return "No node is active yet. Pick the one clients should reach.";
        }

        // A node reports its own address, so this is data from outside the panel: an unparseable or
        // private value means the node is misconfigured, not that the record is wrong.
        if (!IPAddress.TryParse(target, out var address) || !PublicIpAddress.IsPubliclyRoutable(address))
        {
            return $"The active node reports {target}, which is not reachable from the internet. Clients will not connect to it.";
        }

        return resolved.Length == 0
            ? $"{recordName} did not resolve from the panel. If you just changed it, wait for the old TTL to pass."
            : null;
    }
}
