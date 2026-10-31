using System.Globalization;
using System.Text;
using System.Text.Json;
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

        // The panel handing over a copy of itself. A GET because it is a download, and the one
        // GET in the panel that is refused outright to a demo session rather than masked: what
        // comes out is the fleet private key and every client's, in plaintext, and a response
        // whose secrets happened not to be in the mask's substitution table would be a leak
        // rather than a refusal. The demo guard names this path for that reason.
        admin.MapGet("/fleet/export", (FleetBackupService backups, HttpContext context) =>
        {
            var backup = backups.Export(context.User.Identity?.Name);
            var stamp = backup.CreatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            // Named after the fleet and the moment, because the one thing an operator has to be
            // able to do with a drawer full of these is tell them apart - and downloaded as an
            // opaque stream rather than application/json so a browser saves it instead of
            // drawing it on screen.
            return Results.File(
                JsonSerializer.SerializeToUtf8Bytes(backup, ControlJsonContext.Default.FleetBackupFile),
                "application/octet-stream",
                $"awg-panel-{Sanitize(backup.EndpointHost)}-{stamp}.json");
        });

        // And the way back. Wholesale: nothing is merged, because a half-restored panel holds one
        // fleet's identity and another fleet's peers. Posted as the file's own bytes like the
        // legacy import, with `replace` meaning the same thing it means there.
        admin.MapPost("/fleet/restore", async Task<IResult> (
            HttpRequest request,
            FleetBackupService backups,
            HttpContext context) =>
        {
            var replace = request.Query["replace"] == "true";

            FleetBackupFile? backup;
            try
            {
                backup = await JsonSerializer.DeserializeAsync(request.Body, ControlJsonContext.Default.FleetBackupFile);
            }
            catch (JsonException)
            {
                // Said as a refusal rather than a 500: an operator who picked the wrong file in a
                // bad week needs to be told which, not shown a stack trace.
                return Results.BadRequest(new ApiError("backup_unreadable", "That file is not JSON this panel can read."));
            }

            var (result, error) = backups.Restore(backup, replace, context.User.Identity?.Name);
            return result is null ? Results.BadRequest(error) : Results.Ok(result);
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

    /// <summary>A filename an operator can keep in a folder: the endpoint host, with anything a
    /// filesystem would argue about replaced.</summary>
    private static string Sanitize(string host)
    {
        var safe = new string(host.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? "fleet" : safe;
    }
}
