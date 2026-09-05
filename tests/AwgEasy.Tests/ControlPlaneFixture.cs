using System.Net.Http.Json;
using System.Security.Cryptography;
using AwgEasy.Contracts;
using AwgEasy.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AwgEasy.Tests;

/// <summary>
/// Deterministic stand-in for the awg CLI, which is not present on a dev machine.
///
/// Key material is fake but structurally distinct per call, which is all the control plane
/// needs: it never interprets these values, it only stores and forwards them.
/// </summary>
public sealed class FakeKeyGenerator : IAwgKeyGenerator
{
    private int _counter;

    public string GeneratePrivateKey() => $"PRIV{Interlocked.Increment(ref _counter):D4}{new string('=', 40)}";

    public string GeneratePublicKey(string privateKey) => "PUB" + privateKey[4..];

    public string GeneratePresharedKey() => $"PSK{Interlocked.Increment(ref _counter):D4}{new string('=', 40)}";
}

/// <summary>
/// Hosts the real control plane against a throwaway SQLite file so the agent protocol can be
/// exercised end to end, rather than mocked and assumed.
/// </summary>
public sealed class ControlPlaneFixture : WebApplicationFactory<ControlPlaneEntryPoint>, IAsyncLifetime
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "correct-horse-battery-staple";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "awg-tests",
        Guid.NewGuid().ToString("N") + ".db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ControlOptions>();
            services.AddSingleton(new ControlOptions(
                DatabasePath: _databasePath,
                DefaultSubnet: "10.8.0.0/24",
                DefaultListenPort: 51820,
                DefaultClientAllowedIps: "0.0.0.0/0, ::/0",
                DefaultClientDns: "1.1.1.1",
                DefaultEndpointHost: "vpn.example.com",
                BundleLifetime: TimeSpan.FromMinutes(15),
                BootstrapAdminUser: AdminUser,
                BootstrapAdminPassword: AdminPassword,
                LegacyStateImportPath: null));

            services.RemoveAll<IAwgKeyGenerator>();
            services.AddSingleton<IAwgKeyGenerator, FakeKeyGenerator>();
        });

        return base.CreateHost(builder);
    }

    /// <summary>A client that has completed the admin sign-in flow and carries the auth cookie.</summary>
    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminUser, AdminPassword));
        response.EnsureSuccessStatusCode();
        return client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        TryDeleteDatabase();
    }

    private void TryDeleteDatabase()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A lingering SQLite handle is not worth failing a test run over.
            }
        }
    }
}

/// <summary>
/// Minimal stand-in for the node agent: holds a key pair, signs requests the way
/// <c>ControlPlaneClient</c> does, and verifies bundles the way <c>BundleAcceptor</c> does.
/// </summary>
public sealed class TestAgent : IDisposable
{
    private readonly ECDsa _key = BundleSigning.CreateKey();

    public string PublicKey => BundleSigning.ExportPublicKey(_key);

    public string? NodeId { get; private set; }

    public string? PinnedSigningKey { get; private set; }

    public string? PinnedSigningKeyId { get; private set; }

    public async Task<EnrollResponse> EnrollAsync(HttpClient client, string token, string hostname = "test-node")
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token, hostname, PublicKey, "test-agent/1.0"));

        response.EnsureSuccessStatusCode();
        var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        NodeId = enrolled.NodeId;
        PinnedSigningKey = enrolled.ControlSigningPublicKey;
        PinnedSigningKeyId = enrolled.ControlSigningKeyId;
        return enrolled;
    }

    public HttpRequestMessage SignedRequest(HttpMethod method, string path, HttpContent? content = null, string? asNodeId = null)
    {
        var message = new HttpRequestMessage(method, path) { Content = content };
        var body = content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];

        var timestamp = AgentRequestSignature.Timestamp(DateTimeOffset.UtcNow);
        var nonce = AgentRequestSignature.NewNonce();
        var canonical = AgentRequestSignature.BuildCanonicalRequest(method.Method, path, timestamp, nonce, body);

        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NodeHeader, asNodeId ?? NodeId ?? string.Empty);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.TimestampHeader, timestamp);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NonceHeader, nonce);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.SignatureHeader, AgentRequestSignature.Sign(_key, canonical));
        return message;
    }

    /// <summary>Runs the exact checks the real agent runs before touching the interface.</summary>
    public DesiredStateBundle AcceptBundle(SignedBundle envelope, long appliedRevision = 0)
    {
        using var controlKey = BundleSigning.ImportPublicKey(PinnedSigningKey!);
        Assert.True(
            BundleSigning.TryVerify(envelope, controlKey, PinnedSigningKeyId!, ContractsJsonContext.Default.DesiredStateBundle, out var bundle, out var signatureError),
            signatureError.Message);

        Assert.True(
            BundleGuard.TryAccept(bundle, appliedRevision, NodeId!, DateTimeOffset.UtcNow, out var guardError),
            guardError.Message);

        return bundle;
    }

    public void Dispose() => _key.Dispose();
}
