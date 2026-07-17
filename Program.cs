using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedHost
        | ForwardedHeaders.XForwardedProto;

    // Caddy runs in Docker/network environments where the proxy IP is not stable.
    // Keep the backend port private when trusting forwarded headers.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton(AwgEasyOptions.FromEnvironment());
builder.Services.AddSingleton<AwgStateStore>();
builder.Services.AddSingleton<AwgKeyGenerator>();
builder.Services.AddSingleton<AwgConfigWriter>();
builder.Services.AddSingleton<AwgRuntime>();
builder.Services.AddSingleton<ClientShareStore>();
builder.Services.AddHostedService<AwgStartupService>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference("/scalar", options =>
{
    options.Title = "AWG Easy API";
    options.OpenApiRoutePattern = "/openapi/v1.json";
});

var api = app.MapGroup("/api");

api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")))
    .WithName("GetHealth")
    .Produces<HealthResponse>();

api.MapGet("/server", (AwgStateStore store, AwgEasyOptions options) =>
{
    var state = store.Read();
    return TypedResults.Ok(ServerResponse.From(state, options));
})
    .WithName("GetServer")
    .Produces<ServerResponse>();

api.MapGet("/server/runtime", async (AwgRuntime runtime, CancellationToken cancellationToken) =>
{
    var status = await runtime.GetStatusAsync(cancellationToken);
    return TypedResults.Ok(status);
})
    .WithName("GetServerRuntime")
    .Produces<AwgRuntimeStatus>();

api.MapGet("/clients", (AwgStateStore store) =>
{
    var state = store.Read();
    return TypedResults.Ok(state.Clients.Select(ClientResponse.From).ToArray());
})
    .WithName("GetClients")
    .Produces<ClientResponse[]>();

api.MapGet("/clients/events", async (
    HttpContext context,
    AwgStateStore store,
    AwgRuntime runtime,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    await WriteClientStatsEventAsync(context, store, runtime, cancellationToken);

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
    while (await timer.WaitForNextTickAsync(cancellationToken))
    {
        await WriteClientStatsEventAsync(context, store, runtime, cancellationToken);
    }
})
    .WithName("StreamClientStats")
    .ExcludeFromDescription();

api.MapPut("/server/obfuscation", async Task<Results<Ok<ServerResponse>, BadRequest<ApiError>>> (
    ServerObfuscationProfile request,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    AwgEasyOptions options,
    CancellationToken cancellationToken) =>
{
    if (!AwgObfuscationValidator.TryValidateServerProfile(request, out var validationError))
    {
        return TypedResults.BadRequest(validationError);
    }

    var result = store.Update(state =>
    {
        state.ServerObfuscation = request.Normalize();
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return StoreUpdateResult<AwgState>.Ok(state);
    });

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.Ok(ServerResponse.From(result.Value, options));
})
    .WithName("UpdateServerObfuscation")
    .Accepts<ServerObfuscationProfile>("application/json")
    .Produces<ServerResponse>()
    .Produces<ApiError>(StatusCodes.Status400BadRequest);

api.MapPost("/clients", async Task<Results<Created<ClientResponse>, BadRequest<ApiError>>> (
    CreateClientRequest request,
    AwgStateStore store,
    AwgKeyGenerator keys,
    AwgConfigWriter configWriter,
    AwgEasyOptions options,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return TypedResults.BadRequest(new ApiError("client_name_required", "Client name is required."));
    }

    if (!AwgObfuscationValidator.TryValidateClientOverrides(request.Obfuscation, out var validationError))
    {
        return TypedResults.BadRequest(validationError);
    }

    var now = DateTimeOffset.UtcNow;
    var transaction = store.Update(state =>
    {
        state.EnsureServerInitialized(keys, options);

        var normalizedName = request.Name.Trim();
        if (state.Clients.Any(client => string.Equals(client.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return StoreUpdateResult<AwgClient>.Fail(new ApiError("client_name_exists", "Client with this name already exists."));
        }

        if (!state.TryAllocateAddress(options.TunnelSubnet, out var address, out var addressError))
        {
            return StoreUpdateResult<AwgClient>.Fail(addressError);
        }

        var privateKey = keys.GeneratePrivateKey();
        var client = new AwgClient
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name = normalizedName,
            Address = address,
            PrivateKey = privateKey,
            PublicKey = keys.GeneratePublicKey(privateKey),
            PresharedKey = keys.GeneratePresharedKey(),
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            Obfuscation = request.Obfuscation?.Normalize() ?? new ClientObfuscationOverrides()
        };

        state.Clients.Add(client);
        state.UpdatedAt = now;
        return StoreUpdateResult<AwgClient>.Ok(client);
    });

    if (!transaction.Success)
    {
        return TypedResults.BadRequest(transaction.Error);
    }

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.Created($"/api/clients/{transaction.Value.Id}", ClientResponse.From(transaction.Value));
})
    .WithName("CreateClient")
    .Accepts<CreateClientRequest>("application/json")
    .Produces<ClientResponse>(StatusCodes.Status201Created)
    .Produces<ApiError>(StatusCodes.Status400BadRequest);

api.MapDelete("/clients/{id}", async Task<Results<NoContent, NotFound<ApiError>>> (
    string id,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    CancellationToken cancellationToken) =>
{
    var removed = store.Update(state =>
    {
        var client = state.Clients.FirstOrDefault(item => item.Id == id);
        if (client is null)
        {
            return StoreUpdateResult<bool>.Fail(new ApiError("client_not_found", "Client was not found."));
        }

        state.Clients.Remove(client);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return StoreUpdateResult<bool>.Ok(true);
    });

    if (!removed.Success)
    {
        return TypedResults.NotFound(removed.Error);
    }

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.NoContent();
})
    .WithName("DeleteClient")
    .Produces(StatusCodes.Status204NoContent)
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapPut("/clients/{id}", async Task<Results<Ok<ClientResponse>, BadRequest<ApiError>, NotFound<ApiError>>> (
    string id,
    UpdateClientRequest request,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return TypedResults.BadRequest(new ApiError("client_name_required", "Client name is required."));
    }

    var result = store.Update(state =>
    {
        var client = state.Clients.FirstOrDefault(item => item.Id == id);
        if (client is null)
        {
            return StoreUpdateResult<AwgClient>.Fail(new ApiError("client_not_found", "Client was not found."));
        }

        var normalizedName = request.Name.Trim();
        if (state.Clients.Any(item =>
            item.Id != id && string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return StoreUpdateResult<AwgClient>.Fail(new ApiError("client_name_exists", "Client with this name already exists."));
        }

        client.Name = normalizedName;
        client.UpdatedAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = client.UpdatedAt;
        return StoreUpdateResult<AwgClient>.Ok(client);
    });

    if (!result.Success)
    {
        if (result.Error.Code == "client_not_found")
        {
            return TypedResults.NotFound(result.Error);
        }

        return TypedResults.BadRequest(result.Error);
    }

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.Ok(ClientResponse.From(result.Value));
})
    .WithName("UpdateClient")
    .Accepts<UpdateClientRequest>("application/json")
    .Produces<ClientResponse>()
    .Produces<ApiError>(StatusCodes.Status400BadRequest)
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapPost("/clients/{id}/disable", async Task<Results<Ok<ClientResponse>, NotFound<ApiError>>> (
    string id,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    CancellationToken cancellationToken) =>
{
    var result = store.Update(state => ToggleClient(state, id, enabled: false));
    if (!result.Success)
    {
        return TypedResults.NotFound(result.Error);
    }

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.Ok(ClientResponse.From(result.Value));
})
    .WithName("DisableClient")
    .Produces<ClientResponse>()
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapPost("/clients/{id}/enable", async Task<Results<Ok<ClientResponse>, NotFound<ApiError>>> (
    string id,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    CancellationToken cancellationToken) =>
{
    var result = store.Update(state => ToggleClient(state, id, enabled: true));
    if (!result.Success)
    {
        return TypedResults.NotFound(result.Error);
    }

    await configWriter.ApplyAsync(cancellationToken);
    return TypedResults.Ok(ClientResponse.From(result.Value));
})
    .WithName("EnableClient")
    .Produces<ClientResponse>()
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapGet("/clients/{id}/config", Results<ContentHttpResult, NotFound<ApiError>> (
    string id,
    AwgStateStore store,
    AwgEasyOptions options) =>
{
    var state = store.Read();
    var client = state.Clients.FirstOrDefault(item => item.Id == id);
    if (client is null)
    {
        return TypedResults.NotFound(new ApiError("client_not_found", "Client was not found."));
    }

    var config = AwgConfigRenderer.RenderClient(state, client, options);
    return TypedResults.Text(config, "text/plain; charset=utf-8");
})
    .WithName("GetClientConfig")
    .Produces<string>(contentType: "text/plain")
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapPost("/clients/{id}/share", Results<Ok<ClientShareResponse>, NotFound<ApiError>> (
    string id,
    AwgStateStore store,
    ClientShareStore shares) =>
{
    var state = store.Read();
    var client = state.Clients.FirstOrDefault(item => item.Id == id);
    if (client is null)
    {
        return TypedResults.NotFound(new ApiError("client_not_found", "Client was not found."));
    }

    var share = shares.Create(client.Id);
    return TypedResults.Ok(ClientShareResponse.From(share, client));
})
    .WithName("CreateClientShare")
    .Produces<ClientShareResponse>()
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapGet("/shares/{token}", Results<Ok<ClientShareResponse>, NotFound<ApiError>> (
    string token,
    AwgStateStore store,
    ClientShareStore shares) =>
{
    if (!shares.TryGet(token, out var share))
    {
        return TypedResults.NotFound(new ApiError("share_not_found", "Share link was not found or has expired."));
    }

    var state = store.Read();
    var client = state.Clients.FirstOrDefault(item => item.Id == share.ClientId);
    return client is null
        ? TypedResults.NotFound(new ApiError("client_not_found", "Client was not found."))
        : TypedResults.Ok(ClientShareResponse.From(share, client));
})
    .WithName("GetClientShare")
    .Produces<ClientShareResponse>()
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapGet("/shares/{token}/config", Results<FileContentHttpResult, NotFound<ApiError>> (
    string token,
    AwgStateStore store,
    AwgEasyOptions options,
    ClientShareStore shares) =>
{
    if (!shares.TryGet(token, out var share))
    {
        return TypedResults.NotFound(new ApiError("share_not_found", "Share link was not found or has expired."));
    }

    var state = store.Read();
    var client = state.Clients.FirstOrDefault(item => item.Id == share.ClientId);
    if (client is null)
    {
        return TypedResults.NotFound(new ApiError("client_not_found", "Client was not found."));
    }

    var config = AwgConfigRenderer.RenderClient(state, client, options);
    var bytes = Encoding.UTF8.GetBytes(config);
    var fileName = ConfigFileName(client.Name);
    return TypedResults.File(bytes, "text/plain; charset=utf-8", fileName);
})
    .WithName("DownloadSharedClientConfig")
    .Produces(StatusCodes.Status200OK, contentType: "text/plain")
    .Produces<ApiError>(StatusCodes.Status404NotFound);

api.MapGet("/backups/export", Results<FileContentHttpResult, BadRequest<ApiError>> (
    AwgStateStore store,
    AwgEasyOptions options) =>
{
    var state = store.Read();
    var backup = new AwgBackup(1, DateTimeOffset.UtcNow, options, state);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, AppJsonSerializerContext.Default.AwgBackup);
    var fileName = $"awg-easy-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";

    return TypedResults.File(bytes, "application/json", fileName);
})
    .WithName("ExportBackup")
    .Produces(StatusCodes.Status200OK, contentType: "application/json")
    .Produces<ApiError>(StatusCodes.Status400BadRequest);

api.MapPost("/backups/import", async Task<Results<Ok<BackupImportResponse>, BadRequest<ApiError>>> (
    AwgBackup backup,
    AwgStateStore store,
    AwgConfigWriter configWriter,
    AwgEasyOptions options,
    CancellationToken cancellationToken) =>
{
    if (!AwgBackupValidator.TryValidate(backup, options, out var warnings, out var validationError))
    {
        return TypedResults.BadRequest(validationError);
    }

    var now = DateTimeOffset.UtcNow;
    backup.State.UpdatedAt = now;
    store.Replace(backup.State);

    await configWriter.ApplyAsync(cancellationToken);

    var state = store.Read();
    return TypedResults.Ok(new BackupImportResponse(
        ServerResponse.From(state, options),
        backup.Options,
        options,
        warnings));
})
    .WithName("ImportBackup")
    .Accepts<AwgBackup>("application/json")
    .Produces<BackupImportResponse>()
    .Produces<ApiError>(StatusCodes.Status400BadRequest);

app.MapGet("/secret", () => TypedResults.Redirect("/admin", permanent: true))
    .ExcludeFromDescription();

app.MapGet("/admin", Results<FileContentHttpResult, NotFound> (IWebHostEnvironment environment) =>
{
    var path = Path.Combine(environment.WebRootPath, "admin", "index.html");
    if (!File.Exists(path))
    {
        return TypedResults.NotFound();
    }

    return TypedResults.File(File.ReadAllBytes(path), "text/html; charset=utf-8");
})
    .ExcludeFromDescription();

app.MapFallbackToFile("index.html");

app.Run();

static async Task WriteClientStatsEventAsync(HttpContext context, AwgStateStore store, AwgRuntime runtime, CancellationToken cancellationToken)
{
    var state = store.Read();
    var stats = await runtime.GetClientStatsAsync(state.Clients, cancellationToken);
    var json = JsonSerializer.Serialize(stats, AppJsonSerializerContext.Default.ClientStatsResponseArray);

    await context.Response.WriteAsync("event: client-stats\n", cancellationToken);
    await context.Response.WriteAsync("data: ", cancellationToken);
    await context.Response.WriteAsync(json, cancellationToken);
    await context.Response.WriteAsync("\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

static StoreUpdateResult<AwgClient> ToggleClient(AwgState state, string id, bool enabled)
{
    var client = state.Clients.FirstOrDefault(item => item.Id == id);
    if (client is null)
    {
        return StoreUpdateResult<AwgClient>.Fail(new ApiError("client_not_found", "Client was not found."));
    }

    client.Enabled = enabled;
    client.UpdatedAt = DateTimeOffset.UtcNow;
    state.UpdatedAt = client.UpdatedAt;
    return StoreUpdateResult<AwgClient>.Ok(client);
}

static string ConfigFileName(string clientName)
{
    var builder = new StringBuilder(clientName.Length);
    foreach (var character in clientName)
    {
        builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '-');
    }

    var name = builder.ToString().Trim('-', '.');
    return string.IsNullOrWhiteSpace(name) ? "amneziawg.conf" : $"{name}.conf";
}

public sealed record AwgEasyOptions(
    string ConfigPath,
    string InterfaceConfigPath,
    string InterfaceName,
    int AwgPort,
    string TunnelSubnet,
    string ClientAllowedIps,
    string EndpointHost,
    string? ClientDns)
{
    public static AwgEasyOptions FromEnvironment()
    {
        var awgPort = ReadInt("AWG_PORT", 51820);
        return new AwgEasyOptions(
            Environment.GetEnvironmentVariable("AWG_CONFIG_PATH") ?? "/etc/awg-easy/state.json",
            Environment.GetEnvironmentVariable("AWG_INTERFACE_CONFIG_PATH") ?? "/etc/amnezia/amneziawg/awg0.conf",
            Environment.GetEnvironmentVariable("AWG_INTERFACE") ?? "awg0",
            awgPort,
            Environment.GetEnvironmentVariable("AWG_SUBNET") ?? "10.8.0.0/24",
            Environment.GetEnvironmentVariable("AWG_CLIENT_ALLOWED_IPS") ?? "0.0.0.0/0, ::/0",
            Environment.GetEnvironmentVariable("AWG_ENDPOINT_HOST") ?? "127.0.0.1",
            Environment.GetEnvironmentVariable("AWG_CLIENT_DNS"));
    }

    private static int ReadInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

public sealed class AwgStateStore(AwgEasyOptions options)
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();

    public AwgState Read()
    {
        lock (_lock)
        {
            return ReadUnsafe();
        }
    }

    public StoreUpdateResult<T> Update<T>(Func<AwgState, StoreUpdateResult<T>> update)
    {
        lock (_lock)
        {
            var state = ReadUnsafe();
            var result = update(state);
            if (!result.Success)
            {
                return result;
            }

            WriteUnsafe(state);
            return result;
        }
    }

    public void Replace(AwgState state)
    {
        lock (_lock)
        {
            WriteUnsafe(state);
        }
    }

    private AwgState ReadUnsafe()
    {
        if (!File.Exists(options.ConfigPath))
        {
            return new AwgState();
        }

        using var stream = File.OpenRead(options.ConfigPath);
        return JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.AwgState) ?? new AwgState();
    }

    private void WriteUnsafe(AwgState state)
    {
        var directory = Path.GetDirectoryName(options.ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = options.ConfigPath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, state, AppJsonSerializerContext.Default.AwgState);
        }

        File.Move(tempPath, options.ConfigPath, overwrite: true);
    }
}

public sealed class AwgStartupService(AwgStateStore store, AwgKeyGenerator keys, AwgEasyOptions options, AwgConfigWriter configWriter) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        store.Update(state =>
        {
            state.EnsureServerInitialized(keys, options);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            return StoreUpdateResult<bool>.Ok(true);
        });

        await configWriter.ApplyAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class AwgConfigWriter(AwgStateStore store, AwgEasyOptions options, AwgRuntime runtime, ILogger<AwgConfigWriter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = store.Read();
            var config = AwgConfigRenderer.RenderServer(state, options);
            var directory = Path.GetDirectoryName(options.InterfaceConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            logger.LogInformation("Writing AmneziaWG config to {InterfaceConfigPath}.", options.InterfaceConfigPath);
            await File.WriteAllTextAsync(options.InterfaceConfigPath, config, cancellationToken);
            SetOwnerOnlyPermissions(options.InterfaceConfigPath);
            if (await runtime.InterfaceExistsAsync(cancellationToken))
            {
                logger.LogInformation("AmneziaWG interface {InterfaceName} exists. Applying configuration with awg syncconf.", options.InterfaceName);
                await SyncExistingInterfaceAsync(cancellationToken);
            }
            else
            {
                logger.LogInformation("AmneziaWG interface {InterfaceName} is not running. Starting it with awg-quick up.", options.InterfaceName);
                await RunOptionalAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SyncExistingInterfaceAsync(CancellationToken cancellationToken)
    {
        var strip = await RunOptionalAsync("awg-quick", ["strip", options.InterfaceConfigPath], cancellationToken);
        if (strip is null)
        {
            return;
        }

        var syncConfigPath = options.InterfaceConfigPath + ".sync";
        await File.WriteAllTextAsync(syncConfigPath, strip.Output, cancellationToken);
        SetOwnerOnlyPermissions(syncConfigPath);

        var sync = await RunOptionalAsync("awg", ["syncconf", options.InterfaceName, syncConfigPath], cancellationToken);
        if (sync is not null && sync.ExitCode == 0)
        {
            return;
        }

        logger.LogWarning("awg syncconf failed. Falling back to awg-quick up for {InterfaceConfigPath}.", options.InterfaceConfigPath);
        await RunOptionalAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task<ProcessResult?> RunOptionalAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(fileName, arguments, input: null, cancellationToken);
            if (result.ExitCode != 0)
        {
            logger.LogWarning("{Command} exited with code {ExitCode}: {Error}", fileName, result.ExitCode, result.Error);
        }
        else
        {
            logger.LogInformation("{Command} {Arguments} completed successfully.{Output}", fileName, string.Join(' ', arguments), FormatProcessOutput(result));
        }

        return result;
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "{Command} is not available. Configuration was written but interface was not applied.", fileName);
            return null;
        }
    }

    private static string FormatProcessOutput(ProcessResult result)
    {
        var output = (result.Output + result.Error).Trim();
        return string.IsNullOrWhiteSpace(output) ? string.Empty : Environment.NewLine + output;
    }
}

public sealed class AwgRuntime(AwgEasyOptions options, ILogger<AwgRuntime> logger)
{
    private static readonly TimeSpan OnlineHandshakeWindow = TimeSpan.FromMinutes(3);

    public async Task<bool> InterfaceExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("awg", ["show", options.InterfaceName], input: null, cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "awg is not available. Interface state could not be checked.");
            return false;
        }
    }

    public async Task<AwgRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var tools = new AwgToolStatus(
            await GetToolVersionAsync("awg", ["--version"], cancellationToken),
            await GetToolVersionAsync("awg-quick", ["--version"], cancellationToken),
            await GetToolVersionAsync("amneziawg-go", ["--version"], cancellationToken),
            Environment.GetEnvironmentVariable("WG_QUICK_USERSPACE_IMPLEMENTATION"));

        var show = await RunStatusCommandAsync("awg", ["show", options.InterfaceName], cancellationToken);
        var ip = await RunStatusCommandAsync("ip", ["address", "show", "dev", options.InterfaceName], cancellationToken);
        var link = await RunStatusCommandAsync("ip", ["-d", "link", "show", "dev", options.InterfaceName], cancellationToken);
        var backend = DetectBackend(show.ExitCode == 0, link, tools.UserspaceImplementation);
        return new AwgRuntimeStatus(options.InterfaceName, show.ExitCode == 0, backend, tools, show, ip);
    }

    public async Task<ClientStatsResponse[]> GetClientStatsAsync(IEnumerable<AwgClient> clients, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var peerStats = await GetPeerStatsAsync(cancellationToken);

        return clients
            .Select(client =>
            {
                peerStats.TryGetValue(client.PublicKey, out var peer);
                var latestHandshakeAt = peer?.LatestHandshakeAt;
                var online = client.Enabled
                    && latestHandshakeAt.HasValue
                    && now - latestHandshakeAt.Value <= OnlineHandshakeWindow;

                return new ClientStatsResponse(
                    client.Id,
                    latestHandshakeAt,
                    peer?.ReceivedBytes ?? 0,
                    peer?.TransmittedBytes ?? 0,
                    online);
            })
            .ToArray();
    }

    private async Task<Dictionary<string, AwgPeerStats>> GetPeerStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("awg", ["show", options.InterfaceName, "dump"], input: null, cancellationToken);
            if (result.ExitCode != 0)
            {
                logger.LogWarning("awg show {InterfaceName} dump exited with code {ExitCode}: {Error}", options.InterfaceName, result.ExitCode, result.Error);
                return [];
            }

            return ParseDump(result.Output);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "awg is not available. Client traffic stats could not be read.");
            return [];
        }
    }

    private static Dictionary<string, AwgPeerStats> ParseDump(string dump)
    {
        var stats = new Dictionary<string, AwgPeerStats>(StringComparer.Ordinal);
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split('\t');
            if (columns.Length < 8)
            {
                continue;
            }

            var publicKey = columns[0];
            var latestHandshakeAt = TryParseLong(columns[4], out var handshakeUnixSeconds) && handshakeUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(handshakeUnixSeconds)
                : (DateTimeOffset?)null;

            stats[publicKey] = new AwgPeerStats(
                latestHandshakeAt,
                TryParseLong(columns[5], out var receivedBytes) ? receivedBytes : 0,
                TryParseLong(columns[6], out var transmittedBytes) ? transmittedBytes : 0);
        }

        return stats;
    }

    private static bool TryParseLong(string value, out long result)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static async Task<string?> GetToolVersionAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(fileName, arguments, input: null, cancellationToken);
            var text = (result.Output + result.Error).Trim();
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(text) ? text : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<AwgCommandStatus> RunStatusCommandAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(fileName, arguments, input: null, cancellationToken);
            return new AwgCommandStatus(fileName + " " + string.Join(' ', arguments), result.ExitCode, result.Output.Trim(), result.Error.Trim());
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AwgCommandStatus(fileName + " " + string.Join(' ', arguments), -1, string.Empty, ex.Message);
        }
    }

    private static string? DetectBackend(bool isRunning, AwgCommandStatus link, string? userspaceImplementation)
    {
        if (!isRunning)
        {
            return null;
        }

        var linkText = link.Output + Environment.NewLine + link.Error;
        if (link.ExitCode == 0
            && (linkText.Contains("amneziawg", StringComparison.OrdinalIgnoreCase)
                || linkText.Contains("wireguard", StringComparison.OrdinalIgnoreCase)))
        {
            return "Kernel module";
        }

        if (!string.IsNullOrWhiteSpace(userspaceImplementation))
        {
            return $"Userspace ({userspaceImplementation})";
        }

        return "Unknown";
    }
}

public sealed class ClientShareStore
{
    private readonly ConcurrentDictionary<string, ClientShare> _shares = new(StringComparer.Ordinal);

    public ClientShare Create(string clientId)
    {
        RemoveExpired();

        var share = new ClientShare(
            GenerateToken(),
            clientId,
            DateTimeOffset.UtcNow.AddDays(1));

        _shares[share.Token] = share;
        return share;
    }

    public bool TryGet(string token, out ClientShare share)
    {
        RemoveExpired();
        if (_shares.TryGetValue(token, out share!) && share.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        share = default!;
        return false;
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _shares)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _shares.TryRemove(item.Key, out _);
            }
        }
    }

    private static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public sealed class AwgKeyGenerator
{
    public string GeneratePrivateKey() => RunRequired("awg", ["genkey"], input: null);

    public string GeneratePublicKey(string privateKey) => RunRequired("awg", ["pubkey"], privateKey + "\n");

    public string GeneratePresharedKey() => RunRequired("awg", ["genpsk"], input: null);

    private static string RunRequired(string fileName, string[] arguments, string? input)
    {
        var result = ProcessRunner.RunAsync(fileName, arguments, input, CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with code {result.ExitCode}: {result.Error}");
        }

        return result.Output.Trim();
    }
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = input is not null;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}

public static class AwgConfigRenderer
{
    public static string RenderServer(AwgState state, AwgEasyOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Interface]");
        builder.Append("PrivateKey = ").AppendLine(state.ServerPrivateKey);
        builder.Append("Address = ").AppendLine(GetServerAddress(options.TunnelSubnet));
        builder.Append("ListenPort = ").AppendLine(options.AwgPort.ToString(CultureInfo.InvariantCulture));
        AppendServerObfuscation(builder, state.ServerObfuscation);
        builder.AppendLine("PostUp = iptables -A FORWARD -i %i -j ACCEPT; iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE");
        builder.AppendLine("PostDown = iptables -D FORWARD -i %i -j ACCEPT; iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE");

        foreach (var client in state.Clients.Where(item => item.Enabled))
        {
            builder.AppendLine();
            builder.AppendLine("[Peer]");
            builder.Append("PublicKey = ").AppendLine(client.PublicKey);
            builder.Append("PresharedKey = ").AppendLine(client.PresharedKey);
            builder.Append("AllowedIPs = ").Append(client.Address).AppendLine("/32");
        }

        return builder.ToString();
    }

    public static string RenderClient(AwgState state, AwgClient client, AwgEasyOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Interface]");
        builder.Append("PrivateKey = ").AppendLine(client.PrivateKey);
        builder.Append("Address = ").Append(client.Address).Append('/').AppendLine(Ipv4Network.Parse(options.TunnelSubnet).PrefixLength.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.ClientDns))
        {
            builder.Append("DNS = ").AppendLine(options.ClientDns);
        }

        AppendServerObfuscation(builder, state.ServerObfuscation);
        AppendClientObfuscation(builder, client.GetEffectiveObfuscation(state.ServerObfuscation));
        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.Append("PublicKey = ").AppendLine(state.ServerPublicKey);
        builder.Append("PresharedKey = ").AppendLine(client.PresharedKey);
        builder.Append("AllowedIPs = ").AppendLine(options.ClientAllowedIps);
        builder.Append("Endpoint = ").Append(options.EndpointHost).Append(':').AppendLine(options.AwgPort.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("PersistentKeepalive = 25");
        return builder.ToString();
    }

    private static void AppendServerObfuscation(StringBuilder builder, ServerObfuscationProfile? obfuscation)
    {
        if (obfuscation is null)
        {
            return;
        }

        Append(builder, "S1", obfuscation.S1);
        Append(builder, "S2", obfuscation.S2);
        Append(builder, "S3", obfuscation.S3);
        Append(builder, "S4", obfuscation.S4);
        Append(builder, "H1", obfuscation.H1);
        Append(builder, "H2", obfuscation.H2);
        Append(builder, "H3", obfuscation.H3);
        Append(builder, "H4", obfuscation.H4);
    }

    private static void AppendClientObfuscation(StringBuilder builder, ClientObfuscationOverrides? obfuscation)
    {
        if (obfuscation is null)
        {
            return;
        }

        Append(builder, "Jc", obfuscation.Jc);
        Append(builder, "Jmin", obfuscation.Jmin);
        Append(builder, "Jmax", obfuscation.Jmax);
        Append(builder, "I1", obfuscation.I1);
        Append(builder, "I2", obfuscation.I2);
        Append(builder, "I3", obfuscation.I3);
        Append(builder, "I4", obfuscation.I4);
        Append(builder, "I5", obfuscation.I5);
    }

    private static void Append(StringBuilder builder, string key, int? value)
    {
        if (value.HasValue)
        {
            builder.Append(key).Append(" = ").AppendLine(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(key).Append(" = ").AppendLine(value);
        }
    }

    private static string GetServerAddress(string subnet)
    {
        var network = Ipv4Network.Parse(subnet);
        return network.GetAddress(1) + "/" + network.PrefixLength.ToString(CultureInfo.InvariantCulture);
    }
}

public static class AwgObfuscationValidator
{
    public static bool TryValidateServerProfile(ServerObfuscationProfile? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation is null)
        {
            return true;
        }

        if (!ValidateHValues([obfuscation.H1, obfuscation.H2, obfuscation.H3, obfuscation.H4], out error))
        {
            return false;
        }

        if (!ValidateClientOverrides(obfuscation.GetDefaults(), out error))
        {
            return false;
        }

        return true;
    }

    public static bool TryValidateClientOverrides(ClientObfuscationOverrides? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation is null)
        {
            return true;
        }

        return ValidateClientOverrides(obfuscation, out error);
    }

    private static bool ValidateClientOverrides(ClientObfuscationOverrides obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation.Jc is < 1 or > 128)
        {
            error = new ApiError("invalid_obfuscation", "Jc must be between 1 and 128.");
            return false;
        }

        if (obfuscation.Jmin.HasValue != obfuscation.Jmax.HasValue)
        {
            error = new ApiError("invalid_obfuscation", "Jmin and Jmax must be set together.");
            return false;
        }

        if (obfuscation.Jmin.HasValue && !(obfuscation.Jmin.Value >= 0 && obfuscation.Jmin.Value < obfuscation.Jmax!.Value && obfuscation.Jmax.Value <= 1280))
        {
            error = new ApiError("invalid_obfuscation", "Jmin/Jmax must satisfy 0 <= Jmin < Jmax <= 1280.");
            return false;
        }

        return true;
    }

    private static bool ValidateHValues(string?[] values, out ApiError error)
    {
        error = ApiError.Empty;
        var headers = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToArray();
        if (headers.Length != headers.Distinct().Count())
        {
            error = new ApiError("invalid_obfuscation", "H1-H4 values must be unique.");
            return false;
        }

        return true;
    }
}

public static class AwgBackupValidator
{
    public static bool TryValidate(AwgBackup? backup, AwgEasyOptions currentOptions, out string[] warnings, out ApiError error)
    {
        var warningList = new List<string>();
        warnings = [];
        error = ApiError.Empty;

        if (backup is null)
        {
            error = new ApiError("backup_required", "Backup file is required.");
            return false;
        }

        if (backup.Version != 1)
        {
            error = new ApiError("unsupported_backup_version", "Backup version is not supported.");
            return false;
        }

        if (!TryValidateState(backup.State, currentOptions, out warningList, out error))
        {
            return false;
        }

        AddOptionWarning(warningList, "AWG_INTERFACE", backup.Options.InterfaceName, currentOptions.InterfaceName);
        AddOptionWarning(warningList, "AWG_PORT", backup.Options.AwgPort.ToString(CultureInfo.InvariantCulture), currentOptions.AwgPort.ToString(CultureInfo.InvariantCulture));
        AddOptionWarning(warningList, "AWG_SUBNET", backup.Options.TunnelSubnet, currentOptions.TunnelSubnet);
        AddOptionWarning(warningList, "AWG_CLIENT_ALLOWED_IPS", backup.Options.ClientAllowedIps, currentOptions.ClientAllowedIps);
        AddOptionWarning(warningList, "AWG_ENDPOINT_HOST", backup.Options.EndpointHost, currentOptions.EndpointHost);
        AddOptionWarning(warningList, "AWG_CLIENT_DNS", backup.Options.ClientDns ?? string.Empty, currentOptions.ClientDns ?? string.Empty);

        warnings = warningList.ToArray();
        return true;
    }

    private static bool TryValidateState(AwgState? state, AwgEasyOptions currentOptions, out List<string> warnings, out ApiError error)
    {
        warnings = [];
        error = ApiError.Empty;

        if (state is null)
        {
            error = new ApiError("invalid_backup", "Backup state is missing.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.ServerPrivateKey) || string.IsNullOrWhiteSpace(state.ServerPublicKey))
        {
            error = new ApiError("invalid_backup", "Backup does not contain server keys.");
            return false;
        }

        if (!AwgObfuscationValidator.TryValidateServerProfile(state.ServerObfuscation, out error))
        {
            return false;
        }

        Ipv4Network currentNetwork;
        try
        {
            currentNetwork = Ipv4Network.Parse(currentOptions.TunnelSubnet);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            error = new ApiError("invalid_current_subnet", exception.Message);
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var client in state.Clients)
        {
            if (string.IsNullOrWhiteSpace(client.Id)
                || string.IsNullOrWhiteSpace(client.Name)
                || string.IsNullOrWhiteSpace(client.Address)
                || string.IsNullOrWhiteSpace(client.PrivateKey)
                || string.IsNullOrWhiteSpace(client.PublicKey)
                || string.IsNullOrWhiteSpace(client.PresharedKey))
            {
                error = new ApiError("invalid_backup", "Backup contains a client with missing required fields.");
                return false;
            }

            if (!ids.Add(client.Id) || !names.Add(client.Name) || !addresses.Add(client.Address) || !publicKeys.Add(client.PublicKey))
            {
                error = new ApiError("invalid_backup", "Backup contains duplicate clients, addresses or keys.");
                return false;
            }

            if (!AwgObfuscationValidator.TryValidateClientOverrides(client.Obfuscation, out error))
            {
                return false;
            }

            if (!IPAddress.TryParse(client.Address, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                error = new ApiError("invalid_backup", $"Client {client.Name} has an invalid IPv4 address.");
                return false;
            }

            if (!currentNetwork.Contains(address))
            {
                warnings.Add($"Client {client.Name} address {client.Address} is outside current AWG_SUBNET {currentOptions.TunnelSubnet}.");
            }
        }

        return true;
    }

    private static void AddOptionWarning(List<string> warnings, string name, string backupValue, string currentValue)
    {
        if (!string.Equals(backupValue, currentValue, StringComparison.Ordinal))
        {
            warnings.Add($"{name} differs: backup '{backupValue}', current '{currentValue}'.");
        }
    }
}

public sealed record StoreUpdateResult<T>(bool Success, T Value, ApiError Error)
{
    public static StoreUpdateResult<T> Ok(T value) => new(true, value, ApiError.Empty);

    public static StoreUpdateResult<T> Fail(ApiError error) => new(false, default!, error);
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed record ClientStatsResponse(
    string Id,
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes,
    bool Online);

public sealed record AwgPeerStats(
    DateTimeOffset? LatestHandshakeAt,
    long ReceivedBytes,
    long TransmittedBytes);

public sealed record AwgRuntimeStatus(
    string InterfaceName,
    bool IsRunning,
    string? Backend,
    AwgToolStatus Tools,
    AwgCommandStatus AwgShow,
    AwgCommandStatus IpAddress);

public sealed record AwgToolStatus(
    string? Awg,
    string? AwgQuick,
    string? AmneziawgGo,
    string? UserspaceImplementation);

public sealed record AwgCommandStatus(
    string Command,
    int ExitCode,
    string Output,
    string Error);

public sealed record ApiError(string Code, string Message)
{
    public static ApiError Empty { get; } = new(string.Empty, string.Empty);
}

public sealed record HealthResponse(string Status);

public sealed record ClientShare(
    string Token,
    string ClientId,
    DateTimeOffset ExpiresAt);

public sealed record ClientShareResponse(
    string Token,
    string ClientId,
    string ClientName,
    DateTimeOffset ExpiresAt)
{
    public static ClientShareResponse From(ClientShare share, AwgClient client)
        => new(share.Token, client.Id, client.Name, share.ExpiresAt);
}

public sealed record ServerResponse(
    string InterfaceName,
    int AwgPort,
    string TunnelSubnet,
    string ClientAllowedIps,
    string EndpointHost,
    string ServerPublicKey,
    ServerObfuscationProfile? ServerObfuscation,
    int ClientsCount)
{
    public static ServerResponse From(AwgState state, AwgEasyOptions options)
        => new(options.InterfaceName, options.AwgPort, options.TunnelSubnet, options.ClientAllowedIps, options.EndpointHost, state.ServerPublicKey, state.ServerObfuscation, state.Clients.Count);
}

public sealed record ClientResponse(
    string Id,
    string Name,
    string Address,
    string PublicKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ClientObfuscationOverrides? Obfuscation)
{
    public static ClientResponse From(AwgClient client)
        => new(client.Id, client.Name, client.Address, client.PublicKey, client.Enabled, client.CreatedAt, client.UpdatedAt, client.Obfuscation);
}

public sealed record CreateClientRequest(string Name, ClientObfuscationOverrides? Obfuscation);

public sealed record UpdateClientRequest(string Name);

public sealed record AwgBackup(
    int Version,
    DateTimeOffset ExportedAt,
    AwgEasyOptions Options,
    AwgState State);

public sealed record BackupImportResponse(
    ServerResponse Server,
    AwgEasyOptions BackupOptions,
    AwgEasyOptions CurrentOptions,
    string[] Warnings);

public sealed class AwgState
{
    public string ServerPrivateKey { get; set; } = string.Empty;
    public string ServerPublicKey { get; set; } = string.Empty;
    public ServerObfuscationProfile? ServerObfuscation { get; set; }
    public List<AwgClient> Clients { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void EnsureServerInitialized(AwgKeyGenerator keys, AwgEasyOptions options)
    {
        if (string.IsNullOrWhiteSpace(ServerPrivateKey))
        {
            ServerPrivateKey = keys.GeneratePrivateKey();
        }

        if (string.IsNullOrWhiteSpace(ServerPublicKey))
        {
            ServerPublicKey = keys.GeneratePublicKey(ServerPrivateKey);
        }

        Ipv4Network.Parse(options.TunnelSubnet);
    }

    public bool TryAllocateAddress(string subnet, out string address, out ApiError error)
    {
        var network = Ipv4Network.Parse(subnet);
        var used = Clients.Select(client => client.Address).ToHashSet(StringComparer.Ordinal);
        for (var host = 2u; host < network.UsableHosts; host++)
        {
            var candidate = network.GetAddress(host);
            if (!used.Contains(candidate))
            {
                address = candidate;
                error = ApiError.Empty;
                return true;
            }
        }

        address = string.Empty;
        error = new ApiError("subnet_exhausted", "No free client addresses left in configured subnet.");
        return false;
    }
}

public sealed class AwgClient
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PresharedKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ClientObfuscationOverrides? Obfuscation { get; set; }

    public ClientObfuscationOverrides GetEffectiveObfuscation(ServerObfuscationProfile? serverProfile)
        => new()
        {
            Jc = Obfuscation?.Jc ?? serverProfile?.DefaultJc,
            Jmin = Obfuscation?.Jmin ?? serverProfile?.DefaultJmin,
            Jmax = Obfuscation?.Jmax ?? serverProfile?.DefaultJmax,
            I1 = Obfuscation?.I1 ?? serverProfile?.DefaultI1,
            I2 = Obfuscation?.I2 ?? serverProfile?.DefaultI2,
            I3 = Obfuscation?.I3 ?? serverProfile?.DefaultI3,
            I4 = Obfuscation?.I4 ?? serverProfile?.DefaultI4,
            I5 = Obfuscation?.I5 ?? serverProfile?.DefaultI5
        };
}

public sealed record ClientObfuscationOverrides
{
    public int? Jc { get; init; }
    public int? Jmin { get; init; }
    public int? Jmax { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I1 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I2 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I3 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I4 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I5 { get; init; }

    public ClientObfuscationOverrides Normalize()
        => new()
        {
            Jc = Jc,
            Jmin = Jmin,
            Jmax = Jmax,
            I1 = NormalizeString(I1),
            I2 = NormalizeString(I2),
            I3 = NormalizeString(I3),
            I4 = NormalizeString(I4),
            I5 = NormalizeString(I5)
        };

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ServerObfuscationProfile
{
    public int? S1 { get; init; }
    public int? S2 { get; init; }
    public int? S3 { get; init; }
    public int? S4 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H1 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H2 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H3 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H4 { get; init; }
    public int? DefaultJc { get; init; }
    public int? DefaultJmin { get; init; }
    public int? DefaultJmax { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI1 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI2 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI3 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI4 { get; init; }
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI5 { get; init; }

    public ServerObfuscationProfile Normalize()
        => new()
        {
            S1 = S1,
            S2 = S2,
            S3 = S3,
            S4 = S4,
            H1 = NormalizeString(H1),
            H2 = NormalizeString(H2),
            H3 = NormalizeString(H3),
            H4 = NormalizeString(H4),
            DefaultJc = DefaultJc,
            DefaultJmin = DefaultJmin,
            DefaultJmax = DefaultJmax,
            DefaultI1 = NormalizeString(DefaultI1),
            DefaultI2 = NormalizeString(DefaultI2),
            DefaultI3 = NormalizeString(DefaultI3),
            DefaultI4 = NormalizeString(DefaultI4),
            DefaultI5 = NormalizeString(DefaultI5)
        };

    public ClientObfuscationOverrides GetDefaults()
        => new()
        {
            Jc = DefaultJc,
            Jmin = DefaultJmin,
            Jmax = DefaultJmax,
            I1 = DefaultI1,
            I2 = DefaultI2,
            I3 = DefaultI3,
            I4 = DefaultI4,
            I5 = DefaultI5
        };

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record Ipv4Network(uint NetworkAddress, int PrefixLength)
{
    public uint UsableHosts => PrefixLength >= 31 ? 0u : (1u << (32 - PrefixLength)) - 1u;

    public static Ipv4Network Parse(string value)
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"Invalid IPv4 CIDR subnet: {value}");
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix) || prefix is < 16 or > 30)
        {
            throw new InvalidOperationException("AWG_SUBNET prefix must be between /16 and /30.");
        }

        var raw = IpToUInt(ip);
        var mask = uint.MaxValue << (32 - prefix);
        return new Ipv4Network(raw & mask, prefix);
    }

    public string GetAddress(uint hostOffset) => UIntToIp(NetworkAddress + hostOffset).ToString();

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = IpToUInt(address);
        var mask = uint.MaxValue << (32 - PrefixLength);
        return (value & mask) == NetworkAddress;
    }

    private static uint IpToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UIntToIp(uint value)
        => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

public sealed class StringOrNumberJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => Normalize(reader.GetString()),
            JsonTokenType.Number => reader.TryGetInt64(out var longValue)
                ? longValue.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            _ => throw new JsonException("Expected string, number or null.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AwgState))]
[JsonSerializable(typeof(AwgEasyOptions))]
[JsonSerializable(typeof(AwgClient))]
[JsonSerializable(typeof(AwgClient[]))]
[JsonSerializable(typeof(ServerObfuscationProfile))]
[JsonSerializable(typeof(ClientObfuscationOverrides))]
[JsonSerializable(typeof(CreateClientRequest))]
[JsonSerializable(typeof(UpdateClientRequest))]
[JsonSerializable(typeof(AwgBackup))]
[JsonSerializable(typeof(BackupImportResponse))]
[JsonSerializable(typeof(ClientResponse))]
[JsonSerializable(typeof(ClientResponse[]))]
[JsonSerializable(typeof(ServerResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ClientShareResponse))]
[JsonSerializable(typeof(ClientStatsResponse))]
[JsonSerializable(typeof(ClientStatsResponse[]))]
[JsonSerializable(typeof(AwgRuntimeStatus))]
[JsonSerializable(typeof(AwgToolStatus))]
[JsonSerializable(typeof(AwgCommandStatus))]
[JsonSerializable(typeof(ApiError))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
