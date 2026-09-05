using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Fleet-wide settings: the shared identity, the obfuscation profile every node applies,
/// and adoption of an existing single-server deployment.</summary>

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
