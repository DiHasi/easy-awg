using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Fleet-wide settings: the shared identity, the tunnel MTU, the obfuscation profile
/// every node applies, and adoption of an existing single-server deployment.</summary>

public static class FleetApi
{
    public static void MapFleet(this RouteGroupBuilder admin)
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

        // Bumps the revision, unlike the panel's own bookkeeping: the node half of this value
        // travels in the bundle as NodeSettings.Mtu, so it changes what a node runs. Each node
        // takes its interface down once as it picks the change up - AwgInterface compares the
        // running MTU against the bundle, and syncconf cannot apply an interface-level setting.
        admin.MapPut("/fleet/mtu", (
            UpdateTunnelMtuRequest request,
            FleetRepository repository,
            FleetService fleet,
            EventLog events,
            HttpContext context) =>
        {
            if (request.Mtu < FleetService.DefaultTunnelMtu || request.Mtu > FleetService.MaxTunnelMtu)
            {
                return Results.BadRequest(new ApiError(
                    "invalid_mtu",
                    $"MTU must be between {FleetService.DefaultTunnelMtu} and {FleetService.MaxTunnelMtu}."));
            }

            repository.UpdateTunnelMtu(request.Mtu, DateTimeOffset.UtcNow);
            fleet.BumpRevision("tunnel MTU changed");
            events.Record("fleet.mtu_changed", $"Tunnel MTU set to {request.Mtu}.", actor: context.User.Identity?.Name);
            return Results.Ok(fleet.Describe());
        });

        // The one endpoint that replaces the fleet identity, and the only place anything may.
        // Every client config stops working the moment it returns, which is the point: it exists
        // because a node whose disk was read has given up the key pair, every preshared key and
        // the wire format, and until they are replaced the fleet's traffic is readable by whoever
        // read them. The revision moves once, so nodes converge on their next poll with no
        // interface restart - `awg syncconf` carries keys. Clients are not so lucky and have to
        // be handed their new config by hand, which is what the response and the peer list are for.
        admin.MapPost("/fleet/rotate", async Task<IResult> (
            RotateFleetRequest? request,
            FleetRotationService rotation,
            Notifier notifier,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var (result, error) = rotation.Rotate(request?.ReissueClientKeys ?? false, context.User.Identity?.Name);
            if (result is null)
            {
                return Results.BadRequest(error);
            }

            // Announced as well as logged, unlike the panel's other settings: if the operator
            // pressing this is not the person reading the channel, that is the single most
            // important thing the fleet could tell them.
            await notifier.SendAsync(
                new Notification(
                    "fleet.rotated",
                    $"Fleet identity rotated to generation {result.Generation}",
                    $"Rotated by {context.User.Identity?.Name ?? "an admin"}. Every one of {result.Clients.Length} client config(s) "
                    + "has stopped working and must be handed out again. Nodes converge on their next poll.",
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            return Results.Ok(result);
        });

        // Generated here rather than in the browser so key material keeps coming from one place,
        // the same `awg` the fleet identity comes from.
        admin.MapPost("/fleet/header-protection-key", (IAwgKeyGenerator keys)
            => TypedResults.Ok(new GeneratedKeyResponse(keys.GenerateHeaderProtectionKey())));

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
}
