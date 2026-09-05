using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AwgEasy.Control;

/// <summary>Admin sign-in. The session is a HttpOnly cookie; nothing here is reachable without it
/// except the endpoints that establish it.</summary>

public static class AuthApi
{
    public static void MapAuth(this RouteGroupBuilder api)
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
}
