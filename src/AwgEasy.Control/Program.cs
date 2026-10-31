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
builder.Services.AddSingleton<ClientGroupRepository>();
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<UsageRepository>();
builder.Services.AddSingleton<AvailabilityRepository>();
builder.Services.AddSingleton<EventLog>();
builder.Services.AddSingleton<ShareRepository>();
builder.Services.AddSingleton<IAwgKeyGenerator, AwgToolKeyGenerator>();
builder.Services.AddSingleton<FleetService>();
builder.Services.AddSingleton<FleetRotationService>();
builder.Services.AddSingleton<AdminAccounts>();
// The read-only demo account. Both singletons: the pseudonym key is drawn once per process, which
// is what keeps one peer reading as one person for as long as the panel is up, and the mask caches
// what it builds from the tables.
builder.Services.AddSingleton<DemoPseudonyms>();
builder.Services.AddSingleton<DemoMask>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<ProbeRepository>();
builder.Services.AddSingleton<ProbeService>();
builder.Services.AddNotifications(options.Notifications);
// Registered once and handed to the host as the same instance, so the API reads the rounds the
// background loop actually ran rather than a second, idle monitor of its own.
builder.Services.AddSingleton<FailoverMonitor>();
builder.Services.AddSingleton<PeerPresenceService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<FailoverMonitor>());
// Trims both histories on its own clock, so the status reports and probe rounds that write
// them never carry a delete of their own.
builder.Services.AddHostedService<HistoryRetentionService>();
builder.Services.AddSingleton<AgentAuthenticator>();

// The panel pair. The live role lives in the database rather than in this process, so the context
// is a singleton that every request and both halves of the loop read the same answer from.
builder.Services.AddSingleton<PanelLocalStore>();
builder.Services.AddSingleton<PanelContext>();
builder.Services.AddSingleton<PanelRepository>();
builder.Services.AddSingleton<PanelSnapshotRepository>();
// One loop for both roles, handed to the host as the same instance so the API reads the rounds it
// actually ran - the same reason FailoverMonitor is registered this way.
builder.Services.AddSingleton<PanelPairService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PanelPairService>());
builder.Services.AddScoped<PanelPairDescriber>();
// Scoped for the same reason DnsFailoverService is: it reaches the DNS provider, and a singleton
// would pin that provider's resolved address for the life of the process.
builder.Services.AddScoped<PanelPromotionService>();
builder.Services.AddHttpClient<PanelReplicationClient>(client =>
{
    // Generous: a cold standby's first stream carries months of history, and a pull that times
    // out halfway is a pull that has to start again.
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient(HttpPanelWitness.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IPanelWitness, HttpPanelWitness>();
builder.Services.AddSingleton<LegacyStateImporter>();
// Scoped, not a singleton: the Cloudflare updater is a typed HttpClient, and holding one for the
// life of the process would pin its handler - and with it the resolved address of the API - forever.
builder.Services.AddScoped<DnsFailoverService>();
builder.Services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();

// The provider is decided at startup from what is configured, so an unconfigured panel still
// switches nodes - it just tells the operator which record to edit instead of editing it.
if (options.Dns.CloudflareConfigured)
{
    builder.Services.AddHttpClient<IDnsRecordUpdater, CloudflareDnsUpdater>(client =>
    {
        client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        client.Timeout = TimeSpan.FromSeconds(15);
        // The token stays in the environment and is attached here, so nothing else has to carry it.
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Dns.CloudflareApiToken);
    });
}
else
{
    builder.Services.AddSingleton<IDnsRecordUpdater, ManualDnsRecordUpdater>();
}

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// After authentication so a refusal can still be read by a signed-in operator, and before the
// endpoints so a standby cannot reach one that writes. Everything a standby does and does not
// answer is in that one file, on purpose.
app.UsePanelWriteGuard();

// After it, for the same reasons and in the same shape: a demo session may read what a standby
// may read, and changes nothing either. Second because the two refusals mean different things -
// "ask the other panel" comes before "this account never will".
app.UseDemoGuard();

app.MapAdminApi();
app.MapAgentApi();
app.MapProbeAgentApi();
app.MapPanelReplicationApi();

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

    // Fail before touching the database: a half-initialised fleet with a wrong endpoint is
    // harder to recover from than a container that refuses to come up with a clear reason.
    if (options.Validate() is { } configurationError)
    {
        logger.LogCritical("{Error}", configurationError);
        throw new InvalidOperationException(configurationError);
    }

    scope.ServiceProvider.GetRequiredService<Database>().Migrate();

    // Reads the local row, creating it with this panel's own key pair on first run. Before the
    // fleet, because it is what decides whether this panel may create one.
    var panel = scope.ServiceProvider.GetRequiredService<PanelContext>();

    if (panel.IsActive)
    {
        scope.ServiceProvider.GetRequiredService<FleetService>().EnsureInitialized();
    }
    else
    {
        // A standby must not invent an identity. Agents pin the signing key they were enrolled
        // with, so a second identity would be refused by every one of them - and the first
        // snapshot is about to overwrite it anyway. It serves nothing until it has synced, and
        // says so rather than drawing an empty fleet.
        logger.LogInformation(
            "Starting as a standby panel ({PanelId}). It replicates the fleet from the panel in charge and refuses "
            + "every write until it is promoted.",
            panel.PanelId);
    }

    var accounts = scope.ServiceProvider.GetRequiredService<AdminAccounts>();
    if (!accounts.AnyOperatorExists())
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

    // The demo account, which only the panel in charge creates: admin rows arrive at a standby in
    // the snapshot, so one made here would be replaced by the next pull - and a standby refuses
    // every write regardless of who is asking.
    if (options.Demo.IsConfigured)
    {
        if (panel.IsActive)
        {
            EnsureDemoAccount(accounts, options, logger);
        }
    }
    else if (!string.IsNullOrWhiteSpace(options.Demo.User) || !string.IsNullOrWhiteSpace(options.Demo.Password))
    {
        // Half-configured. Worth saying out loud: the symptom is an account that simply is not
        // there, which looks like the feature not working rather than like a missing variable.
        logger.LogWarning(
            "The demo account needs both AWG_DEMO_USER and AWG_DEMO_PASSWORD, and only one is set. "
            + "No demo account has been created.");
    }

    // One-shot adoption of an existing single-server deployment, pointed at its state.json.
    // Never on a standby: importing there would write a fleet the next snapshot replaces, and for
    // the length of that window the two panels would disagree about who the clients are.
    if (panel.IsActive && !string.IsNullOrWhiteSpace(options.LegacyStateImportPath) && File.Exists(options.LegacyStateImportPath))
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

/// <summary>
/// Creates the demo account if it is configured and not there yet.
///
/// Never touches an account that already exists. An operator who points AWG_DEMO_USER at a name
/// that is already a full admin has a configuration mistake, and the two ways to resolve it for
/// them are both worse than saying so: making their account read-only could lock them out of the
/// panel, and leaving a full admin behind a password published next to a demo link is the failure
/// this whole feature exists to avoid.
/// </summary>
static void EnsureDemoAccount(AdminAccounts accounts, ControlOptions options, ILogger logger)
{
    var username = options.Demo.User!;
    if (!accounts.Exists(username))
    {
        accounts.Create(username, options.Demo.Password!, demo: true);
        return;
    }

    if (!accounts.IsDemo(username))
    {
        logger.LogWarning(
            "AWG_DEMO_USER is '{Username}', but an account with that name already exists and is a full "
            + "admin. It has been left exactly as it is: nothing here will quietly take an operator's "
            + "own account away, and nothing will hand a demo password a full one. Pick another name for "
            + "the demo account, or delete that row.",
            username);
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
