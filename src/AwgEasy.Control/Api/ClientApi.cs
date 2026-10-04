using System.Globalization;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Client management. Every change that alters the peer list bumps the fleet revision so
/// nodes converge on it; a rename deliberately does not, because it changes no peer material.</summary>

public static class ClientApi
{
    public static void MapClients(this RouteGroupBuilder admin)
    {
        admin.MapGet("/clients", (ClientRepository clients) =>
            TypedResults.Ok(clients.List().Select(ClientResponse.From).ToArray()));

        admin.MapGet("/clients/stats", (ClientRepository clients, NodeRepository nodes) =>
        {
            var stats = nodes.AggregatePeerStats();
            var now = DateTimeOffset.UtcNow;

            return TypedResults.Ok(clients.List().Select(client => Stats(client, stats, now)).ToArray());
        });

        // The counters belong to the kernel and cannot be zeroed without tearing the peer down, so
        // this records where they stand and the panel reports the difference from here on.
        admin.MapPost("/clients/{id}/stats/reset", (
            string id,
            ClientRepository clients,
            NodeRepository nodes,
            EventLog events,
            HttpContext context) =>
        {
            var client = clients.Find(id);
            if (client is null)
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            var now = DateTimeOffset.UtcNow;
            nodes.ResetPeerCounters(client.PublicKey, now);
            events.Record("client.stats_reset", $"Traffic counters for {client.Name} reset.", actor: context.User.Identity?.Name);

            return Results.Ok(Stats(client, nodes.AggregatePeerStats(), now));
        });

        admin.MapPost("/clients", (
            CreateClientRequest request,
            ClientRepository clients,
            ClientGroupRepository groups,
            FleetService fleet,
            IAwgKeyGenerator keys,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("client_name_required", "Client name is required."));
            }

            if (request.GroupId is { } groupId && groups.Find(groupId) is null)
            {
                return Results.BadRequest(new ApiError("group_not_found", "Group was not found."));
            }

            if (!AwgObfuscationValidator.TryValidateClientOverrides(request.Obfuscation, out var error))
            {
                return Results.BadRequest(error);
            }

            var name = request.Name.Trim();
            if (clients.NameExists(name))
            {
                return Results.BadRequest(new ApiError("client_name_exists", "A client with this name already exists."));
            }

            var current = fleet.Current;
            if (!AddressAllocator.TryAllocate(current.Subnet, clients.UsedAddresses(), out var address, out var allocationError))
            {
                return Results.BadRequest(allocationError);
            }

            var now = DateTimeOffset.UtcNow;
            var privateKey = keys.GeneratePrivateKey();
            var client = new ClientRecord(
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                name,
                address,
                privateKey,
                keys.GeneratePublicKey(privateKey),
                keys.GeneratePresharedKey(),
                Enabled: true,
                request.Obfuscation?.Normalize(),
                now,
                now,
                request.GroupId);

            clients.Insert(client);
            fleet.BumpRevision($"client {name} created");
            events.Record("client.created", $"Client {name} created at {address}.", actor: context.User.Identity?.Name);

            return Results.Created($"/api/clients/{client.Id}", ClientResponse.From(client));
        });

        admin.MapPut("/clients/{id}", (
            string id,
            UpdateClientRequest request,
            ClientRepository clients,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("client_name_required", "Client name is required."));
            }

            var name = request.Name.Trim();
            if (clients.NameExists(name, excludingId: id))
            {
                return Results.BadRequest(new ApiError("client_name_exists", "A client with this name already exists."));
            }

            if (!clients.Rename(id, name, DateTimeOffset.UtcNow))
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            // A rename changes no peer material, so nodes do not need a new revision for it.
            events.Record("client.renamed", $"Client renamed to {name}.", actor: context.User.Identity?.Name);
            return Results.Ok(ClientResponse.From(clients.Find(id)!));
        });

        admin.MapPost("/clients/{id}/enable", (string id, ClientRepository clients, FleetService fleet, EventLog events, HttpContext context)
            => SetEnabled(id, true, clients, fleet, events, context));

        admin.MapPost("/clients/{id}/disable", (string id, ClientRepository clients, FleetService fleet, EventLog events, HttpContext context)
            => SetEnabled(id, false, clients, fleet, events, context));

        admin.MapDelete("/clients/{id}", (string id, ClientRepository clients, NodeRepository nodes, FleetService fleet, EventLog events, HttpContext context) =>
        {
            var client = clients.Find(id);
            if (client is null || !clients.Delete(id))
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            nodes.ForgetPeer(client.PublicKey);
            fleet.BumpRevision($"client {client.Name} deleted");
            events.Record("client.deleted", $"Client {client.Name} deleted.", actor: context.User.Identity?.Name);
            return Results.NoContent();
        });

        admin.MapPost("/clients/{id}/share", (
            string id,
            ClientRepository clients,
            ShareRepository shares,
            EventLog events,
            HttpContext context) =>
        {
            var client = clients.Find(id);
            if (client is null)
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            var token = shares.Create(client.Id, DateTimeOffset.UtcNow);
            var url = $"{context.Request.Scheme}://{context.Request.Host}/share/{token}";

            events.Record("client.shared", $"Share link created for {client.Name}.", actor: context.User.Identity?.Name);
            return Results.Ok(new ClientShareResponse(token, client.Name, DateTimeOffset.UtcNow.Add(ShareRepository.Lifetime), url));
        });

        admin.MapGet("/clients/{id}/config", (string id, ClientRepository clients, FleetService fleet) =>
        {
            var client = clients.Find(id);
            if (client is null)
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            var current = fleet.Current;
            var config = ClientConfigRenderer.Render(current, client, current.EndpointHost);
            return Results.File(Encoding.UTF8.GetBytes(config), ClientConfigRenderer.ContentType, ClientConfigRenderer.FileName(client.Name));
        });
    }

    /// <summary>A peer with no handshake in three minutes is not carrying traffic: AmneziaWG rekeys
    /// well inside that, so a longer silence means the tunnel is idle or gone.</summary>
    private static ClientStatsResponse Stats(
        ClientRecord client,
        IReadOnlyDictionary<string, PeerTotals> stats,
        DateTimeOffset now)
    {
        stats.TryGetValue(client.PublicKey, out var peer);
        var online = client.Enabled
            && peer.Handshake.HasValue
            && now - peer.Handshake.Value <= TimeSpan.FromMinutes(3);

        return new ClientStatsResponse(client.Id, peer.Handshake, peer.Rx, peer.Tx, online, peer.NodeId, peer.ResetAt);
    }

    private static IResult SetEnabled(
        string id,
        bool enabled,
        ClientRepository clients,
        FleetService fleet,
        EventLog events,
        HttpContext context)
    {
        var client = clients.Find(id);
        if (client is null || !clients.SetEnabled(id, enabled, DateTimeOffset.UtcNow))
        {
            return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
        }

        var state = enabled ? "enabled" : "disabled";
        fleet.BumpRevision($"client {client.Name} {state}");
        events.Record($"client.{state}", $"Client {client.Name} {state}.", actor: context.User.Identity?.Name);

        return Results.Ok(ClientResponse.From(clients.Find(id)!));
    }
}
