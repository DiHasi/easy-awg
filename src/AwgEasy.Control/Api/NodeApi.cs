using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Node management: enrollment tokens, fleet status, revocation and the audit log.</summary>

public static class NodeApi
{
    public static void MapNodes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes", (NodeRepository nodes, FleetService fleet, DnsFailoverService failover, FailoverMonitor monitor) =>
        {
            var current = fleet.Current;
            var activeNodeId = failover.ActiveNodeId;
            var list = nodes.List();
            var health = monitor.Assess(list, DateTimeOffset.UtcNow);
            return TypedResults.Ok(list
                .Select(node => NodeResponse.From(node, current.Revision, current.IdentityRevision, activeNodeId, health[node.Id]))
                .ToArray());
        });

        // The per-node half of a node's configuration. Two kinds of field in one request, and the
        // difference decides whether the fleet revision moves: the egress interface and the MTU
        // are handed to that node in its bundle, so changing one changes what it runs; the name
        // and the endpoint host are notes here, like a client rename.
        //
        // The revision is fleet-wide, so a per-node change makes every node re-fetch. They apply
        // a bundle whose bytes have not changed for them, which costs one syncconf each - while
        // not bumping it would mean the node this is about never hears: the bundle is served
        // against an ETag of the revision, so an unchanged revision is answered with a 304.
        admin.MapPut("/nodes/{id}", (
            string id,
            UpdateNodeRequest request,
            NodeRepository nodes,
            FleetService fleet,
            DnsFailoverService failover,
            FailoverMonitor monitor,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("node_name_required", "Node name is required."));
            }

            if (!NodeOverrides.TryRead(request.EndpointHost, request.EgressInterface, request.Mtu, out var overrides, out var error))
            {
                return Results.BadRequest(error);
            }

            if (nodes.Find(id) is not { } existing)
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            var name = request.Name.Trim();
            var before = new NodeOverrides(existing.EndpointHost, existing.EgressInterface, existing.Mtu);
            if (!nodes.UpdateSettings(id, name, overrides))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            var actor = context.User.Identity?.Name;
            if (overrides.ChangesBundle(before))
            {
                // The node takes its interface down once as it picks a new MTU up, exactly as a
                // fleet-wide change would: syncconf cannot apply an interface-level setting.
                fleet.BumpRevision($"node {name} settings changed");
                events.Record(
                    "node.settings_changed",
                    $"Node settings changed: MTU {Describe(overrides.Mtu, "fleet value")}, egress {Describe(overrides.EgressInterface, "detected")}.",
                    actor: actor,
                    nodeId: id);
            }
            else if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                events.Record("node.renamed", $"Node is now '{name}'.", actor: actor, nodeId: id);
            }
            else if (!string.Equals(before.EndpointHost, overrides.EndpointHost, StringComparison.Ordinal))
            {
                // Worth a line, unlike a save that changed nothing at all: the log is read to find
                // out what an operator did, not how many times they pressed Save.
                events.Record(
                    "node.settings_changed",
                    $"Endpoint host is now {Describe(overrides.EndpointHost, "unset")}.",
                    actor: actor,
                    nodeId: id);
            }

            var node = nodes.Find(id)!;
            var health = monitor.Assess([node], DateTimeOffset.UtcNow);
            var current = fleet.Current;
            return Results.Ok(NodeResponse.From(node, current.Revision, current.IdentityRevision, failover.ActiveNodeId, health[node.Id]));
        });

        // Which source addresses this node hands out, and what they are called. Both halves are
        // bundle-visible - the default travels in it, and a label decides which address a peer's
        // rule names - so any change here moves the revision.
        //
        // Its own endpoint, and not offered at enrollment: an operator cannot label addresses
        // nobody knows about yet, and which ones a node carries arrives with its first report.
        admin.MapPut("/nodes/{id}/egress", (
            string id,
            UpdateNodeEgressRequest request,
            NodeRepository nodes,
            FleetService fleet,
            DnsFailoverService failover,
            FailoverMonitor monitor,
            EventLog events,
            HttpContext context) =>
        {
            if (!NodeEgressPlan.TryRead(request.DefaultAddress, request.Labels, out var plan, out var error))
            {
                return Results.BadRequest(error);
            }

            if (nodes.Find(id) is not { } existing)
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            var before = NodeEgressPlan.Read(existing.EgressPlan);
            if (!nodes.SetEgressPlan(id, plan.Serialize()))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            if (plan.ChangesBundle(before))
            {
                fleet.BumpRevision($"node {existing.Name} egress changed");
                events.Record(
                    "node.egress_changed",
                    $"Egress addresses changed: default {Describe(plan.DefaultAddress, "kernel choice")}, "
                    + $"{plan.Labels.Length.ToString(CultureInfo.InvariantCulture)} labelled.",
                    actor: context.User.Identity?.Name,
                    nodeId: id);
            }

            var updated = nodes.Find(id)!;
            var state = monitor.Assess([updated], DateTimeOffset.UtcNow);
            var fleetState = fleet.Current;
            return Results.Ok(NodeResponse.From(updated, fleetState.Revision, fleetState.IdentityRevision, failover.ActiveNodeId, state[updated.Id]));
        });

        // Not a fleet change either: which node automatic failover prefers decides nothing any
        // node runs, so the revision stays put.
        admin.MapPut("/nodes/{id}/failover", (
            string id,
            UpdateNodeFailoverRequest request,
            NodeRepository nodes,
            FleetService fleet,
            DnsFailoverService failover,
            FailoverMonitor monitor,
            EventLog events,
            HttpContext context) =>
        {
            if (request.Priority is < 0 or > 1000)
            {
                return Results.BadRequest(new ApiError("failover_priority_invalid", "Priority must be between 0 and 1000."));
            }

            if (!nodes.SetFailoverPreferences(id, request.Priority, request.AutoFailover))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            events.Record(
                "node.failover_preferences",
                request.AutoFailover
                    ? $"Failover priority set to {request.Priority}."
                    : "Excluded from automatic failover.",
                actor: context.User.Identity?.Name,
                nodeId: id);

            var node = nodes.Find(id)!;
            var health = monitor.Assess([node], DateTimeOffset.UtcNow);
            var current = fleet.Current;
            return Results.Ok(NodeResponse.From(node, current.Revision, current.IdentityRevision, failover.ActiveNodeId, health[node.Id]));
        });

        // Manual failover. Deliberately not a fleet change: the record every client config already
        // names simply starts resolving to a different node, so nothing is reissued and no node
        // re-applies anything - which is why the fleet revision stays where it is.
        admin.MapPost("/nodes/{id}/activate", async Task<IResult> (
            string id,
            DnsFailoverService failover,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var (status, error) = await failover.ActivateAsync(id, context.User.Identity?.Name, cancellationToken);
            if (status is not null)
            {
                return Results.Ok(status);
            }

            return error!.Code switch
            {
                "node_not_found" => Results.NotFound(error),
                // The fleet is fine and the request was well formed; the provider is what refused.
                "dns_update_failed" => Results.Json(error, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.BadRequest(error)
            };
        });

        // The per-node settings travel on the token, so a node that needs a narrower MTU or a
        // named egress interface has them in the first bundle it fetches rather than being
        // corrected after it has already brought an interface up.
        admin.MapPost("/nodes/tokens", (
            CreateNodeRequest request,
            EnrollmentService enrollment,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("node_name_required", "Node name is required."));
            }

            if (!NodeOverrides.TryRead(request.EndpointHost, request.EgressInterface, request.Mtu, out var overrides, out var error))
            {
                return Results.BadRequest(error);
            }

            var controlUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            return Results.Ok(enrollment.CreateToken(request.Name.Trim(), controlUrl, overrides: overrides));
        });

        admin.MapPost("/nodes/{id}/revoke", (string id, NodeRepository nodes, EventLog events, HttpContext context) =>
        {
            if (!nodes.SetRevoked(id, true))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            // Revocation is a flag checked on every agent request, so it takes effect on the very
            // next call - no certificate revocation list to distribute and wait for.
            events.Record("node.revoked", "Node access revoked.", actor: context.User.Identity?.Name, nodeId: id);
            return Results.NoContent();
        });

        admin.MapDelete("/nodes/{id}", (
            string id,
            NodeRepository nodes,
            ProbeRepository probes,
            AvailabilityRepository availability,
            DnsFailoverService failover,
            EventLog events,
            HttpContext context) =>
        {
            if (!nodes.Delete(id))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            probes.ForgetNode(id);
            availability.ForgetNode(id);

            // The DNS record may still point here - the panel cannot unpoint it on the operator's
            // behalf - but it must stop claiming a node it has forgotten is the active one.
            failover.ForgetIfActive(id);

            events.Record("node.deleted", "Node removed from the fleet.", actor: context.User.Identity?.Name, nodeId: id);
            return Results.NoContent();
        });

        admin.MapGet("/events", (EventLog events) => TypedResults.Ok(events.Recent().ToArray()));
    }

    /// <summary>An override as an event line reads it, naming what applies when there is none.</summary>
    private static string Describe(object? value, string fallback) => value?.ToString() ?? fallback;
}
