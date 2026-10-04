using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Groups and the arrangement of the peer list. One group is one person; the peers in it are the
/// devices that person holds.
///
/// Nothing here bumps the fleet revision, and that is the point: a group is the operator's own
/// filing, it carries no peer material, and no node has anything to re-apply when a peer is
/// dragged somewhere else. It is stored server-side rather than in the browser so the operator
/// sees the same list from every device they sign in from.
/// </summary>
public static class ClientGroupApi
{
    public static void MapClientGroups(this RouteGroupBuilder admin)
    {
        admin.MapGet("/groups", (ClientGroupRepository groups) =>
            TypedResults.Ok(groups.List().Select(ClientGroupResponse.From).ToArray()));

        admin.MapPost("/groups", (
            CreateClientGroupRequest request,
            ClientGroupRepository groups,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("group_name_required", "Group name is required."));
            }

            var name = request.Name.Trim();
            if (groups.NameExists(name))
            {
                return Results.BadRequest(new ApiError("group_name_exists", "A group with this name already exists."));
            }

            var group = groups.Insert(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, DateTimeOffset.UtcNow);
            events.Record("group.created", $"Group {name} created.", actor: context.User.Identity?.Name);

            return Results.Created($"/api/groups/{group.Id}", ClientGroupResponse.From(group));
        });

        admin.MapPut("/groups/{id}", (
            string id,
            UpdateClientGroupRequest request,
            ClientGroupRepository groups,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("group_name_required", "Group name is required."));
            }

            var name = request.Name.Trim();
            if (groups.NameExists(name, excludingId: id))
            {
                return Results.BadRequest(new ApiError("group_name_exists", "A group with this name already exists."));
            }

            if (!groups.Rename(id, name, DateTimeOffset.UtcNow))
            {
                return Results.NotFound(new ApiError("group_not_found", "Group was not found."));
            }

            events.Record("group.renamed", $"Group renamed to {name}.", actor: context.User.Identity?.Name);
            return Results.Ok(ClientGroupResponse.From(groups.Find(id)!));
        });

        // Deleting a group keeps every config it held, ungrouped. Losing a label must never
        // disconnect anybody, so this is not a cascade and does not ask to be confirmed as one.
        admin.MapDelete("/groups/{id}", (
            string id,
            ClientGroupRepository groups,
            EventLog events,
            HttpContext context) =>
        {
            var group = groups.Find(id);
            if (group is null || !groups.Delete(id, DateTimeOffset.UtcNow))
            {
                return Results.NotFound(new ApiError("group_not_found", "Group was not found."));
            }

            events.Record("group.deleted", $"Group {group.Name} deleted; its peers are ungrouped.", actor: context.User.Identity?.Name);
            return Results.NoContent();
        });

        // A literal segment wins over "{id}" in routing, so this does not collide with the rename.
        admin.MapPut("/groups/order", (
            ReorderClientGroupsRequest request,
            ClientGroupRepository groups) =>
        {
            if (request.Ids is null || request.Ids.Length != request.Ids.Distinct(StringComparer.Ordinal).Count())
            {
                return Results.BadRequest(new ApiError("arrangement_invalid", "The group order must list each group at most once."));
            }

            if (!groups.Reorder(request.Ids, DateTimeOffset.UtcNow))
            {
                return Results.BadRequest(new ApiError("group_not_found", "The order names a group that no longer exists."));
            }

            return Results.Ok(groups.List().Select(ClientGroupResponse.From).ToArray());
        });

        // The whole arrangement in one call, applied as a unit: a drag that half-landed would
        // leave the operator reading a list neither they nor the panel arranged. The answer is
        // the list as it now stands, so a browser whose drag raced another one adopts the truth
        // rather than its own guess.
        admin.MapPut("/clients/arrangement", (
            ArrangeClientsRequest request,
            ClientRepository clients,
            ClientGroupRepository groups) =>
        {
            if (request.Groups is null || request.Groups.Any(bucket => bucket.ClientIds is null))
            {
                return Results.BadRequest(new ApiError("arrangement_invalid", "Every bucket must name the peers it holds."));
            }

            var placed = request.Groups.SelectMany(bucket => bucket.ClientIds).ToArray();
            if (placed.Length != placed.Distinct(StringComparer.Ordinal).Count())
            {
                return Results.BadRequest(new ApiError("arrangement_duplicate_peer", "A peer can only sit in one group."));
            }

            var known = groups.List().Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            if (request.Groups.Any(bucket => bucket.GroupId is { } groupId && !known.Contains(groupId)))
            {
                return Results.BadRequest(new ApiError("group_not_found", "The arrangement names a group that no longer exists."));
            }

            var buckets = request.Groups
                .Select(bucket => new ClientPlacement(bucket.GroupId, bucket.ClientIds))
                .ToArray();

            if (!clients.Arrange(buckets, DateTimeOffset.UtcNow))
            {
                return Results.BadRequest(new ApiError("client_not_found", "The arrangement names a peer that no longer exists."));
            }

            return Results.Ok(clients.List().Select(ClientResponse.From).ToArray());
        });
    }
}
