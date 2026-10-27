using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Whether the request being served is a demo session's. Written once by
/// <see cref="DemoGuard"/> and read by the one endpoint that has to answer differently.</summary>
public static class DemoRequest
{
    private const string Key = "awg.demo-session";

    public static bool IsDemo(this HttpContext context)
        => context.Items.TryGetValue(Key, out var marked) && marked is true;

    internal static void Mark(HttpContext context, bool demo) => context.Items[Key] = demo;
}

/// <summary>
/// Keeps a demo session read-only, and keeps the fleet's own names and addresses out of what it
/// is served, in one place.
///
/// One place for the same reason <see cref="AdminApi"/> keeps the authorization boundary in one
/// short method and <see cref="PanelWriteGuard"/> keeps the standby's: it has to be possible to
/// read at a glance what a demo visitor can reach, because what sits behind this line is a real
/// fleet with real people on it. A rule spread across forty endpoints is a rule that will be
/// forgotten the next time one is added - and here the thing that would be forgotten is not a
/// refusal, which announces itself, but a leak, which does not.
///
/// So the refusal is a method-level rule rather than a list of write endpoints, and the masking is
/// a pass over the response body rather than a mapping per endpoint. Both are deliberately blunt:
/// an endpoint added later is refused if it writes and masked if it reads, without being named
/// here. The cost is a buffered response and a walk over its JSON for the demo session only, which
/// is a price worth paying exactly once per visitor who is looking rather than working.
///
/// What a demo session may do: read every page, download a config - rendered from stand-in
/// material by <see cref="DemoMask.RenderConfig"/>, so it is a real config belonging to nobody -
/// and sign out. What it may not: anything that changes the fleet, and the metrics endpoint,
/// which is a scrape rather than a page and whose output is text this pass does not read.
/// </summary>
public static class DemoGuard
{
    /// <summary>
    /// Reachable whatever the session is: signing in and out, and the endpoint a container's own
    /// health check calls. Signing out has to be here explicitly - it is a POST, and refusing
    /// every POST would leave a demo visitor unable to leave.
    /// </summary>
    private static readonly string[] AlwaysAllowed =
    [
        "/api/health",
        "/api/auth/"
    ];

    public static IApplicationBuilder UseDemoGuard(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                // The panel's own frontend. A demo session gets the same pages an operator does -
                // that is the whole point of it - and they carry no fleet data of their own.
                await next(context);
                return;
            }

            // Read from the row rather than from a claim in the cookie, so an account that was
            // made read-only is read-only on its next request. Only a cookie-authenticated
            // request pays for the read: an agent or a probe signs its requests and never reaches
            // this branch.
            var username = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null;
            var isDemo = username is not null
                && context.RequestServices.GetRequiredService<AdminAccounts>().IsDemo(username);

            DemoRequest.Mark(context, isDemo);

            if (!isDemo || Matches(path, AlwaysAllowed))
            {
                await next(context);
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                await RefuseAsync(context, new ApiError(
                    "demo_read_only",
                    "This is the demo account. It shows a real fleet and can change nothing about it - "
                    + "every name, address and key you can see has been replaced on the way out."));

                return;
            }

            if (path.StartsWith("/api/metrics", StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(context, new ApiError(
                    "demo_read_only",
                    "The metrics endpoint is a scrape rather than a page: it answers in Prometheus text, "
                    + "which the demo's masking does not read, so it is not served to a demo session."));

                return;
            }

            // The one read whose body is not JSON. It renders its own config from stand-in
            // material, so there is nothing here to rewrite - and a config that had been
            // scrubbed line by line afterwards would be a worse answer than one that was never
            // anybody's to begin with.
            //
            // This also covers a share link's config, which is deliberate rather than missed: a
            // share link is anonymous, so the token is the whole credential and a demo session
            // holding one is simply a visitor who was handed it. A demo session cannot create
            // one - that is a write - so the only way it has a token is that somebody gave it one.
            if (path.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            await MaskAsync(context, next);
        });

    /// <summary>
    /// Buffers the response and rewrites it. Buffered rather than streamed because the mask is a
    /// walk over the whole JSON document: a name can be anywhere in it, and a pass that had to
    /// decide chunk by chunk would be one that could be split through the middle of a name.
    /// </summary>
    private static async Task MaskAsync(HttpContext context, RequestDelegate next)
    {
        var destination = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = destination;
        }

        buffer.Position = 0;

        // Anything that is not a JSON document goes back untouched: an empty body, a 304, a
        // redirect. Nothing else in the admin API answers in another format, and if something
        // ever does, the right place to decide what happens to it is here rather than in a pass
        // that would have to guess at its shape.
        var isJson = context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
        if (!isJson || buffer.Length == 0)
        {
            await buffer.CopyToAsync(destination);
            return;
        }

        var masked = context.RequestServices.GetRequiredService<DemoMask>().Json(Encoding.UTF8.GetString(buffer.ToArray()));
        var bytes = Encoding.UTF8.GetBytes(masked);

        // The length changes with the names, so the one the endpoint set is no longer true.
        context.Response.ContentLength = bytes.Length;
        await destination.WriteAsync(bytes);
    }

    private static bool Matches(string path, string[] prefixes)
        => prefixes.Any(prefix => prefix.EndsWith('/')
            ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            : string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 403 rather than the 503 a standby answers with, and the difference is the point. A standby
    /// turns a request away because it arrived at the wrong panel, which is transient and worth
    /// retrying elsewhere; a demo session is refused because of who it is, which no amount of
    /// waiting changes. A caller - the panel's own frontend included - should be able to tell
    /// "ask the other panel" from "this account will never be allowed to".
    /// </summary>
    private static Task RefuseAsync(HttpContext context, ApiError error)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(error, ControlJsonContext.Default.ApiError);
    }
}
