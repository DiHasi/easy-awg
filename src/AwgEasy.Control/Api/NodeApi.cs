using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Node management: enrollment tokens, fleet status, revocation and the audit log.</summary>

public static class NodeApi
{
    public static void MapNodes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes", (NodeRepository nodes, FleetService fleet, DnsFailoverService failover) =>
        {
            var revision = fleet.Current.Revision;
            var activeNodeId = failover.ActiveNodeId;
            return TypedResults.Ok(nodes.List().Select(node => NodeResponse.From(node, revision, activeNodeId)).ToArray());
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

        admin.MapDelete("/nodes/{id}", (string id, NodeRepository nodes, DnsFailoverService failover, EventLog events, HttpContext context) =>
        {
            if (!nodes.Delete(id))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            // The DNS record may still point here - the panel cannot unpoint it on the operator's
            // behalf - but it must stop claiming a node it has forgotten is the active one.
            failover.ForgetIfActive(id);

            events.Record("node.deleted", "Node removed from the fleet.", actor: context.User.Identity?.Name, nodeId: id);
            return Results.NoContent();
        });

        admin.MapGet("/events", (EventLog events) => TypedResults.Ok(events.Recent().ToArray()));
    }
}
