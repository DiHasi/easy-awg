using System.Globalization;
using System.Security.Claims;
using System.Text;
using AwgEasy.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AwgEasy.Control;

public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok"))).AllowAnonymous();

        MapAuth(api);
        MapShares(api);

        // Everything below requires a signed-in admin. The fleet identity and every client
        // private key are reachable through this group.
        var admin = api.MapGroup(string.Empty).RequireAuthorization();

        MapFleet(admin);
        MapClients(admin);
        MapNodes(admin);
    }

    private static void MapAuth(RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", async (LoginRequest request, AdminAccounts accounts, EventLog events, HttpContext context) =>
        {
            if (!accounts.Verify(request.Username, request.Password))
            {
                events.Record("admin.login_failed", $"Failed sign-in for {request.Username}.");
                return Results.Unauthorized();
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, request.Username)],
                CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            events.Record("admin.login", $"Admin {request.Username} signed in.", actor: request.Username);
            return Results.Ok(new HealthResponse("ok"));
        }).AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new HealthResponse("ok"));
        }).AllowAnonymous();

        api.MapGet("/auth/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new HealthResponse(context.User.Identity.Name ?? "admin"))
            : Results.Unauthorized()).AllowAnonymous();
    }

    /// <summary>
    /// Anonymous by design: the point of a share link is to hand a config to someone who has no
    /// account. Knowledge of the 24-byte token is the only credential, and it reveals nothing
    /// beyond that one client's config.
    /// </summary>
    private static void MapShares(RouteGroupBuilder api)
    {
        api.MapGet("/shares/{token}", (string token, ShareRepository shares, ClientRepository clients) =>
        {
            var share = shares.Resolve(token, DateTimeOffset.UtcNow);
            var client = share is null ? null : clients.Find(share.ClientId);

            return client is null
                ? Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."))
                : Results.Ok(new PublicShareResponse(client.Name, share!.ExpiresAt));
        }).AllowAnonymous();

        api.MapGet("/shares/{token}/config", (string token, ShareRepository shares, ClientRepository clients, FleetService fleet) =>
        {
            var share = shares.Resolve(token, DateTimeOffset.UtcNow);
            var client = share is null ? null : clients.Find(share.ClientId);
            if (client is null)
            {
                return Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."));
            }

            var current = fleet.Current;
            var config = ClientConfigRenderer.Render(current, client, current.EndpointHost);
            return Results.File(Encoding.UTF8.GetBytes(config), "text/plain; charset=utf-8", ClientConfigRenderer.FileName(client.Name));
        }).AllowAnonymous();
    }

    private static void MapFleet(RouteGroupBuilder admin)
    {
        admin.MapGet("/fleet", (FleetService fleet) => TypedResults.Ok(fleet.Describe()));

        admin.MapPut("/fleet/obfuscation", (
            ServerObfuscationProfile request,
            FleetRepository repository,
            FleetService fleet,
            EventLog events,
            HttpContext context) =>
        {
            if (!AwgObfuscationValidator.TryValidateServerProfile(request, out var error))
            {
                return Results.BadRequest(error);
            }

            repository.UpdateObfuscation(request.Normalize(), DateTimeOffset.UtcNow);
            fleet.BumpRevision("obfuscation changed");
            events.Record("fleet.obfuscation_changed", "Fleet obfuscation profile updated.", actor: context.User.Identity?.Name);
            return Results.Ok(fleet.Describe());
        });

        admin.MapPost("/fleet/import", async (
            HttpRequest request,
            LegacyStateImporter importer) =>
        {
            var replace = request.Query["replace"] == "true";
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();

            var (result, error) = importer.Import(json, replace);
            return result is null ? Results.BadRequest(error) : Results.Ok(result);
        });
    }

    private static void MapClients(RouteGroupBuilder admin)
    {
        admin.MapGet("/clients", (ClientRepository clients) =>
            TypedResults.Ok(clients.List().Select(ClientResponse.From).ToArray()));

        admin.MapGet("/clients/stats", (ClientRepository clients, NodeRepository nodes) =>
        {
            var stats = nodes.AggregatePeerStats();
            var now = DateTimeOffset.UtcNow;

            return TypedResults.Ok(clients.List().Select(client =>
            {
                stats.TryGetValue(client.PublicKey, out var peer);
                var online = client.Enabled
                    && peer.Handshake.HasValue
                    && now - peer.Handshake.Value <= TimeSpan.FromMinutes(3);

                return new ClientStatsResponse(client.Id, peer.Handshake, peer.Rx, peer.Tx, online, peer.NodeId);
            }).ToArray());
        });

        admin.MapPost("/clients", (
            CreateClientRequest request,
            ClientRepository clients,
            FleetService fleet,
            IAwgKeyGenerator keys,
            EventLog events,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("client_name_required", "Client name is required."));
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
                now);

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

        admin.MapDelete("/clients/{id}", (string id, ClientRepository clients, FleetService fleet, EventLog events, HttpContext context) =>
        {
            var client = clients.Find(id);
            if (client is null || !clients.Delete(id))
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

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
            return Results.File(Encoding.UTF8.GetBytes(config), "text/plain; charset=utf-8", ClientConfigRenderer.FileName(client.Name));
        });
    }

    private static void MapNodes(RouteGroupBuilder admin)
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
