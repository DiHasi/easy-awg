using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Keeps a standby panel read-only, in one place.
///
/// The reason it is one place is the same reason <see cref="AdminApi"/> keeps the authorization
/// boundary in one short method: it should be possible to read, at a glance, exactly what a
/// standby will and will not do. A rule spread across forty endpoints is a rule that will be
/// forgotten the next time one is added.
///
/// What a standby refuses, and why each one matters:
///
/// <list type="bullet">
/// <item>Every write to the fleet. Addresses are allocated by the control plane because they must
/// be unique fleet-wide, and two panels allocating out of one subnet is the fleet quietly
/// breaking. The same argument covers client keys, nodes, groups and rotation.</item>
/// <item>The agent and probe APIs, entirely. A standby's view of the fleet is as fresh as its last
/// pull, and a status report arriving here would be written into a database that is about to be
/// overwritten by the next snapshot. Nodes are fail-static, so an agent that reaches a standby
/// and is turned away simply re-applies its cached bundle and keeps serving traffic - which is
/// the designed behaviour for an unreachable panel, not a degradation.</item>
/// <item>Handing out a client config, even though that is a GET. Rendering one stamps
/// <c>config_generation</c>, and a stamp written here never reaches the panel in charge - which
/// would then believe nobody holds a current config when somebody does. Share links go the same
/// way and for the same reason.</item>
/// <item>Everything but its own pages, until it has synced once. A panel with no fleet state has
/// an empty clients table, and a page that drew it would be describing a fleet that does not
/// exist.</item>
/// </list>
///
/// What it allows is reads, signing in, and the handful of calls that act on the panel itself -
/// because the panel an operator can still reach is the one that has to be able to take over.
/// </summary>
public static class PanelWriteGuard
{
    /// <summary>
    /// Reachable in either role, in any method: signing in, and the one endpoint a container's
    /// own health check calls.
    ///
    /// <c>/api/metrics</c> is deliberately not here. It is a GET, so a synced standby answers it
    /// from its own copy of the data - which is what keeps a scrape working across a switch - but
    /// one that has replicated nothing has no fleet to describe and says so rather than throwing.
    /// </summary>
    private static readonly string[] AlwaysAllowed =
    [
        "/api/health",
        "/api/auth/"
    ];

    /// <summary>
    /// The writes a standby does accept, each one about itself rather than about the fleet: taking
    /// over, standing down, the hold, and running a replication round now. The hold in particular
    /// has to work here - a brake that needs the other panel to be reachable is not a brake.
    /// </summary>
    private static readonly string[] SelfWrites =
    [
        "/api/panel/promote",
        "/api/panel/demote",
        "/api/panel/hold",
        "/api/panel/sync"
    ];

    public static IApplicationBuilder UsePanelWriteGuard(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                // Static files and the install script. Serving the panel's own frontend from a
                // standby is the point: it is what an operator looks at to decide.
                await next(context);
                return;
            }

            var panel = context.RequestServices.GetRequiredService<PanelContext>();
            if (panel.IsActive || Matches(path, AlwaysAllowed))
            {
                await next(context);
                return;
            }

            // The replication endpoints police themselves - "hello" has to be answerable in either
            // role, because it is how a superseded panel is told that it has been.
            if (path.StartsWith("/api/v1/panels", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (path.StartsWith("/api/v1/agents", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/v1/probes", StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(context, new ApiError(
                    "panel_not_active",
                    "This panel is a standby and does not publish configuration. Point the agent at the panel in charge; "
                    + "until then it keeps applying its cached bundle and serving traffic."));

                return;
            }

            if (Matches(path, SelfWrites))
            {
                await next(context);
                return;
            }

            var isRead = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
            if (!isRead)
            {
                await RefuseAsync(context, new ApiError(
                    "panel_standby",
                    "This panel is a standby, so it cannot change the fleet - addresses are allocated by one panel only, "
                    + "and two panels allocating out of the same subnet would break it. Make your change on the panel in "
                    + "charge, or promote this one first."));

                return;
            }

            // Reads that write. Rendering a config stamps the generation it was rendered at, and a
            // stamp made here never reaches the panel in charge.
            if (path.EndsWith("/config", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/shares", StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(context, new ApiError(
                    "panel_standby",
                    "Handing out a config records which fleet generation it was issued at, and this panel is a standby, "
                    + "so that record would never reach the panel in charge. Issue it there."));

                return;
            }

            // A panel that has never synced has no fleet to describe. Its own pages still work:
            // that is where the operator finds out why.
            if (!panel.State.HasFleetState && !path.StartsWith("/api/panel", StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(context, new ApiError(
                    "panel_never_synced",
                    "This standby has not replicated the fleet yet, so there is nothing to show. Check the pair's status "
                    + "for what is stopping it."));

                return;
            }

            await next(context);
        });

    private static bool Matches(string path, string[] prefixes)
        => prefixes.Any(prefix => prefix.EndsWith('/')
            ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            : string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 503 rather than 403: the request was legitimate and the caller is allowed to make it, just
    /// not here. It is the same answer a node gets from an unreachable panel, which is what lets
    /// an agent treat it as transient and keep serving traffic rather than giving up on its
    /// identity.
    /// </summary>
    private static Task RefuseAsync(HttpContext context, ApiError error)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return context.Response.WriteAsJsonAsync(error, ControlJsonContext.Default.ApiError);
    }
}
