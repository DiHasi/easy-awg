using AwgEasy.Control;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var options = ControlOptions.FromEnvironment();
builder.Services.AddSingleton(options);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlJsonContext.Default);
});

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "awg_admin";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        // Always over HTTPS in production; relaxed only so the panel can be tried over plain HTTP locally.
        cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        cookie.SlidingExpiration = true;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(12);

        // This is an API, not a server-rendered site: answer with status codes rather than
        // redirecting to a login page that does not exist server-side.
        cookie.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<FleetRepository>();
builder.Services.AddSingleton<ClientRepository>();
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<EventLog>();
builder.Services.AddSingleton<ShareRepository>();
builder.Services.AddSingleton<IAwgKeyGenerator, AwgToolKeyGenerator>();
builder.Services.AddSingleton<FleetService>();
builder.Services.AddSingleton<AdminAccounts>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<AgentAuthenticator>();
builder.Services.AddSingleton<LegacyStateImporter>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapAdminApi();
app.MapAgentApi();

// The Nuxt panel is a client-side app served from the same origin; unknown paths fall through
// to its shell so routes like /nodes resolve on the client.
app.MapFallbackToFile("index.html");

Bootstrap(app);

app.Run();

static void Bootstrap(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var options = scope.ServiceProvider.GetRequiredService<ControlOptions>();
    var logger = app.Logger;

    scope.ServiceProvider.GetRequiredService<Database>().Migrate();
    scope.ServiceProvider.GetRequiredService<FleetService>().EnsureInitialized();

    var accounts = scope.ServiceProvider.GetRequiredService<AdminAccounts>();
    if (!accounts.AnyExists())
    {
        if (!string.IsNullOrWhiteSpace(options.BootstrapAdminUser) && !string.IsNullOrWhiteSpace(options.BootstrapAdminPassword))
        {
            accounts.Create(options.BootstrapAdminUser, options.BootstrapAdminPassword);
        }
        else
        {
            // Refusing to start would be worse: an operator who mistypes the env vars could not
            // get in at all. Warn loudly instead - the admin API stays locked either way.
            logger.LogWarning(
                "No admin account exists and AWG_ADMIN_USER/AWG_ADMIN_PASSWORD are not set. "
                + "The admin API will reject every request until an account is created.");
        }
    }

    // One-shot adoption of an existing single-server deployment, pointed at its state.json.
    if (!string.IsNullOrWhiteSpace(options.LegacyStateImportPath) && File.Exists(options.LegacyStateImportPath))
    {
        var importer = scope.ServiceProvider.GetRequiredService<LegacyStateImporter>();
        var (result, error) = importer.Import(File.ReadAllText(options.LegacyStateImportPath), replaceExistingClients: false);
        if (result is null)
        {
            logger.LogWarning("Legacy state import skipped: {Code} - {Message}", error.Code, error.Message);
        }
        else
        {
            logger.LogInformation("Imported {Count} clients from legacy state at revision {Revision}.", result.ClientsImported, result.Revision);
        }
    }
}

namespace AwgEasy.Control
{
    /// <summary>
    /// Marker naming this assembly for the integration-test host. A public partial Program would
    /// collide with the agent's own top-level entry point when both are referenced.
    /// </summary>
    public sealed class ControlPlaneEntryPoint;
}
