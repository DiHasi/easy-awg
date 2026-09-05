using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Node management: enrollment tokens, fleet status, revocation and the audit log.</summary>

public static class NodeApi
{
    public static void MapNodes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes", (NodeRepository nodes, FleetService fleet) =>
        {
            var revision = fleet.Current.Revision;
            return TypedResults.Ok(nodes.List().Select(node => NodeResponse.From(node, revision)).ToArray());
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

        admin.MapDelete("/nodes/{id}", (string id, NodeRepository nodes, EventLog events, HttpContext context) =>
        {
            if (!nodes.Delete(id))
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            events.Record("node.deleted", "Node removed from the fleet.", actor: context.User.Identity?.Name, nodeId: id);
            return Results.NoContent();
        });

        admin.MapGet("/events", (EventLog events) => TypedResults.Ok(events.Recent().ToArray()));
    }
}
