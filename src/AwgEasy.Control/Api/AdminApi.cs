namespace AwgEasy.Control;

/// <summary>
/// Composes the HTTP surface.
///
/// The authorization boundary lives here and only here: everything under <c>/api</c> requires a
/// signed-in admin except the three groups mapped before it. Keeping that split visible in one
/// short method is the point - it should be obvious at a glance what is reachable anonymously,
/// because the fleet identity and every client private key sit behind this line.
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Anonymous, deliberately and exhaustively.
        api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok"))).AllowAnonymous();
        api.MapAuth();
        api.MapShares();

        // Everything past this point requires a session.
        var admin = api.MapGroup(string.Empty).RequireAuthorization();

        admin.MapFleet();
        admin.MapClients();
        admin.MapNodes();
    }
}
