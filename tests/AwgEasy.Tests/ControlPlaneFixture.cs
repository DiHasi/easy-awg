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

    /// <summary>Unlike the others this one is validated as a real base64 32-byte key before it is
    /// accepted, so the stand-in has to produce one.</summary>
    public string GenerateHeaderProtectionKey()
    {
        var key = new byte[32];
        key[0] = (byte)Interlocked.Increment(ref _counter);
        return Convert.ToBase64String(key);
    }
}

/// <summary>
/// Stand-in DNS provider. Records what the panel asked for instead of editing a real zone, and can
/// be told to refuse, which is the case that must never leave the panel claiming a node is active.
/// </summary>
public sealed class RecordingDnsUpdater : IDnsRecordUpdater
{
    public string ProviderName => "test";

    public bool IsConfigured { get; set; } = true;

    public DnsUpdateOutcome Outcome { get; set; } = DnsUpdateOutcome.Applied;

    public List<DnsRecordTarget> Applied { get; } = [];

    public DnsRecordTarget? Last => Applied.Count == 0 ? null : Applied[^1];

    public Task<DnsUpdateResult> PointAsync(DnsRecordTarget target, CancellationToken cancellationToken)
    {
        if (Outcome == DnsUpdateOutcome.Failed)
        {
            return Task.FromResult(new DnsUpdateResult(
                DnsUpdateOutcome.Failed,
                Error: new ApiError("dns_update_failed", "The stand-in provider was told to refuse.")));
        }

        Applied.Add(target);
        return Task.FromResult(new DnsUpdateResult(Outcome, "recorded"));
    }

    /// <summary>What the pre-flight check reports. Null means the record could be moved.</summary>
    public ApiError? CheckError { get; set; }

    public Task<ApiError?> CheckAsync(string recordName, CancellationToken cancellationToken)
        => Task.FromResult(CheckError);
}

/// <summary>Collects what the panel would have announced, instead of posting it anywhere.</summary>
public sealed class RecordingNotificationChannel : INotificationChannel
{
    private readonly List<Notification> _sent = [];

    public string Name => "test";

    public IReadOnlyList<Notification> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(notification);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Keeps real name resolution out of the suite: what the panel sees is what a test says.</summary>
public sealed class StubHostAddressResolver : IHostAddressResolver
{
    public string[] Addresses { get; set; } = [];

    public Task<string[]> ResolveAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(Addresses);
}

/// <summary>
/// Hosts the real control plane against a throwaway SQLite file so the agent protocol can be
/// exercised end to end, rather than mocked and assumed.
/// </summary>
public sealed class ControlPlaneFixture : WebApplicationFactory<ControlPlaneEntryPoint>, IAsyncLifetime
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "correct-horse-battery-staple";

    public RecordingDnsUpdater Dns { get; } = new();

    public StubHostAddressResolver Resolver { get; } = new();

    public RecordingNotificationChannel Notifications { get; } = new();

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
                LegacyStateImportPath: null,
                Dns: new DnsFailoverOptions(RecordName: null, Ttl: 60, CloudflareApiToken: null, CloudflareZoneId: null),
                // No grace and no cooldown, so a test decides when a round runs - through
                // POST /api/failover/evaluate - and what it sees. The background loop is pushed
                // out of the way for the same reason.
                Failover: new FailoverOptions(
                    CheckInterval: TimeSpan.FromHours(1),
                    Grace: TimeSpan.Zero,
                    Cooldown: TimeSpan.Zero,
                    AgentStaleAfter: TimeSpan.FromMinutes(5),
                    ProbeStaleAfter: TimeSpan.FromMinutes(5),
                    ProbeInterval: TimeSpan.FromSeconds(60),
                    ProbeHandshakeTimeout: TimeSpan.FromSeconds(15),
                    ProbeCheckUrls: FailoverOptions.DefaultProbeCheckUrls,
                    ProbeTrafficTimeout: TimeSpan.FromSeconds(8)),
                Notifications: new NotificationOptions(null, null, null),
                Usage: new UsageOptions(UsageOptions.DefaultRetentionDays)));

            services.AddSingleton<INotificationChannel>(Notifications);

            services.RemoveAll<IAwgKeyGenerator>();
            services.AddSingleton<IAwgKeyGenerator, FakeKeyGenerator>();

            services.RemoveAll<IDnsRecordUpdater>();
            services.AddSingleton<IDnsRecordUpdater>(Dns);

            services.RemoveAll<IHostAddressResolver>();
            services.AddSingleton<IHostAddressResolver>(Resolver);
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

    /// <summary>
    /// Enrolls as the real agent does, reporting the bundle schema it understands. Pass an older
    /// <paramref name="bundleSchemaVersion"/> to stand in for a node that has not been upgraded.
    /// </summary>
    public async Task<EnrollResponse> EnrollAsync(
        HttpClient client,
        string token,
        string hostname = "test-node",
        int bundleSchemaVersion = DesiredStateBundle.CurrentSchemaVersion)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token, hostname, PublicKey, "test-agent/1.0", bundleSchemaVersion));

        response.EnsureSuccessStatusCode();
        var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        NodeId = enrolled.NodeId;
        PinnedSigningKey = enrolled.ControlSigningPublicKey;
        PinnedSigningKeyId = enrolled.ControlSigningKeyId;
        return enrolled;
    }

    /// <summary>Enrolls as a probe does: same request, its own endpoint, and a probe id in place of a node id.</summary>
    public async Task<HttpResponseMessage> EnrollAsProbeAsync(HttpClient client, string token, string hostname = "test-probe")
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/probes/enroll",
            new EnrollRequest(token, hostname, PublicKey, "test-probe/1.0"));

        if (response.IsSuccessStatusCode)
        {
            var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;
            NodeId = enrolled.NodeId;
            PinnedSigningKey = enrolled.ControlSigningPublicKey;
            PinnedSigningKeyId = enrolled.ControlSigningKeyId;
        }

        return response;
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
