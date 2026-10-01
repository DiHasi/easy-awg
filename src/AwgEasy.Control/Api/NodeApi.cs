using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Node management: enrollment tokens, fleet status, revocation and the audit log.</summary>

public static class NodeApi
{
    public static void MapNodes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes", (NodeRepository nodes, FleetService fleet, DnsFailoverService failover, FailoverMonitor monitor) =>
        {
            var revision = fleet.Current.Revision;
            var activeNodeId = failover.ActiveNodeId;
            var list = nodes.List();
            var health = monitor.Assess(list, DateTimeOffset.UtcNow);
            return TypedResults.Ok(list.Select(node => NodeResponse.From(node, revision, activeNodeId, health[node.Id])).ToArray());
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
            return Results.Ok(NodeResponse.From(node, fleet.Current.Revision, failover.ActiveNodeId, health[node.Id]));
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

        admin.MapPost("/nodes/tokens", (
            CreateNodeRequest request,
            EnrollmentService enrollment,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("node_name_required", "Node name is required."));
            }

            var controlUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            return Results.Ok(enrollment.CreateToken(request.Name.Trim(), controlUrl));
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

        admin.MapDelete("/nodes/{id}", (string id, NodeRepository nodes, ProbeRepository probes, DnsFailoverService failover, EventLog events, HttpContext context) =>
        {
            if (!nodes.Delete(id))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            probes.ForgetNode(id);

            // The DNS record may still point here - the panel cannot unpoint it on the operator's
            // behalf - but it must stop claiming a node it has forgotten is the active one.
            failover.ForgetIfActive(id);

            events.Record("node.deleted", "Node removed from the fleet.", actor: context.User.Identity?.Name, nodeId: id);
            return Results.NoContent();
        });

        admin.MapGet("/events", (EventLog events) => TypedResults.Ok(events.Recent().ToArray()));
    }
}
