using System.Globalization;
using System.Security.Claims;
using AwgEasy.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AwgEasy.Control;

/// <summary>
/// Admin sign-in. The session is a HttpOnly cookie; nothing here is reachable without it except
/// the endpoints that establish it.
///
/// Choosing a password lives in <see cref="AdminAccountApi"/> rather than here, and that is not
/// tidiness. Both guards wave this group through whatever the method - a demo visitor and a
/// standby panel still have to be able to sign in and out - so an endpoint that wrote to the
/// admins table from here would be one a read-only account could call and one a standby would
/// apply to a database its next snapshot overwrites.
/// </summary>
public static class AuthApi
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", async (
            LoginRequest request,
            AdminAccounts accounts,
            LoginThrottle throttle,
            EventLog events,
            HttpContext context) =>
        {
            var now = DateTimeOffset.UtcNow;
            var address = context.SourceAddress();

            // Before the hash, not after: verifying costs the panel a few hundred milliseconds of
            // CPU by design, so a refusal that is free to issue is what keeps unlimited guessing
            // from becoming unlimited load. A refused attempt is deliberately not counted either,
            // or an attacker who kept hammering would hold the limit open forever and the
            // operator would never get their fifteen minutes back.
            if (throttle.Check(request.Username, address, now) is { Allowed: false } refusal)
            {
                return Throttled(context, refusal);
            }

            if (!accounts.Verify(request.Username, request.Password))
            {
                var tripped = throttle.RecordFailure(request.Username, address, now);
                events.Record("admin.login_failed", $"Failed sign-in for {request.Username}.");
                if (tripped)
                {
                    // Once per lockout rather than once per guess: an attacker who keeps going
                    // would otherwise bury every other event under their own attempts, and the
                    // log is what the operator reads afterwards to find out what happened.
                    events.Record(
                        "admin.login_throttled",
                        $"Sign-in attempts for {request.Username} refused: too many failures in "
                        + $"{LoginThrottle.Window.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes.");
                }

                return Results.Unauthorized();
            }

            throttle.RecordSuccess(request.Username, address);

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

        // The demo flag comes from the request rather than from a second lookup: the demo guard
        // has already read the row by the time any endpoint runs, and it reads it per request so
        // an account made read-only is read-only at once.
        api.MapGet("/auth/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new SessionResponse(context.User.Identity.Name ?? "admin", context.IsDemo()))
            : Results.Unauthorized()).AllowAnonymous();
    }

    /// <summary>
    /// Where an attempt came from, as far as the panel can tell. Forwarded headers are honoured
    /// for everybody - they have to be, or every request from behind the operator's own TLS
    /// terminator would read as coming from it - so this is evidence of the honest caller and
    /// nothing more; <see cref="LoginThrottle"/> says what rests on it and what does not.
    /// </summary>
    internal static string? SourceAddress(this HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// 429 with the wait, rather than another 401. A caller cannot tell a wrong password from a
    /// refused attempt otherwise, and the panel's own login form has something worth saying: the
    /// password may well be right, and there is nothing to do but wait.
    /// </summary>
    private static IResult Throttled(HttpContext context, LoginVerdict refusal)
    {
        var seconds = (int)Math.Ceiling(refusal.RetryAfter.TotalSeconds);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return Results.Json(
            new ApiError(
                "login_throttled",
                $"Too many failed sign-in attempts. Try again in {Math.Max(1, seconds / 60)} minute(s)."),
            ControlJsonContext.Default.ApiError,
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
