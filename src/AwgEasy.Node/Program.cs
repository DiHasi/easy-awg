using System.Text.Json;
using System.Text.Json.Serialization;
using AwgEasy.Contracts;
using AwgEasy.Node;

// Dry run: verify a bundle and print the config it would produce, touching nothing.
// Lets a node be validated before it is ever pointed at a live fleet, and makes the
// render/verify path testable on a machine with no awg tooling.
if (DryRun.TryHandle(args, out var exitCode))
{
    return exitCode;
}

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NodeJsonContext.Default);
});

var options = NodeOptions.FromEnvironment();
builder.WebHost.UseUrls(options.HealthUrl);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AgentIdentityStore>();
builder.Services.AddSingleton<BundleStore>();
builder.Services.AddSingleton<BundleAcceptor>();
builder.Services.AddSingleton<EgressInterfaceResolver>();
builder.Services.AddSingleton<AwgRuntime>();
builder.Services.AddSingleton<AwgInterface>();
builder.Services.AddSingleton<NodeHealth>();
builder.Services.AddHttpClient<ControlPlaneClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<ReconcileService>();

var app = builder.Build();

// The only endpoint the agent serves, bound to loopback by default: the node opens no
// management surface to the internet.
app.MapGet("/health", (NodeHealth health) => TypedResults.Ok(health.Snapshot()));

app.Logger.LogInformation(
    "awg-node {Version} starting. interface={Interface} state={State} control={Control}",
    AgentVersion.Current,
    options.InterfaceName,
    options.StatePath,
    options.ControlUrl ?? "<offline>");

app.Run();
return 0;

internal static class DryRun
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        var index = Array.IndexOf(args, "--render-bundle");
        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: awg-node --render-bundle <signed-bundle.json> [--control-key <base64url-spki>] [--egress <iface>]");
            exitCode = 2;
            return true;
        }

        var bundlePath = args[index + 1];
        var controlKey = ReadArg(args, "--control-key");
        var egress = ReadArg(args, "--egress") ?? "eth0";

        if (!File.Exists(bundlePath))
        {
            Console.Error.WriteLine($"Bundle file not found: {bundlePath}");
            exitCode = 2;
            return true;
        }

        var envelope = JsonSerializer.Deserialize(File.ReadAllText(bundlePath), NodeJsonContext.Default.SignedBundle);
        if (envelope is null)
        {
            Console.Error.WriteLine("Bundle file did not deserialize.");
            exitCode = 2;
            return true;
        }

        DesiredStateBundle bundle;
        if (controlKey is null)
        {
            Console.Error.WriteLine("WARNING: --control-key not given, signature NOT verified. Rendering payload as-is.");
            var json = Base64Url.Decode(envelope.Payload);
            bundle = JsonSerializer.Deserialize(json, NodeJsonContext.Default.DesiredStateBundle)!;
        }
        else
        {
            using var key = BundleSigning.ImportPublicKey(controlKey);
            if (!BundleSigning.TryVerify(envelope, key, envelope.KeyId, ContractsJsonContext.Default.DesiredStateBundle, out bundle, out var signatureError))
            {
                Console.Error.WriteLine($"Signature check failed: {signatureError.Code} - {signatureError.Message}");
                exitCode = 1;
                return true;
            }

            Console.Error.WriteLine($"Signature OK (key {envelope.KeyId}).");
        }

        if (!BundleGuard.TryAccept(bundle, appliedRevision: 0, bundle.NodeId, DateTimeOffset.UtcNow, out var guardError))
        {
            Console.Error.WriteLine($"Bundle rejected: {guardError.Code} - {guardError.Message}");
            exitCode = 1;
            return true;
        }

        Console.Error.WriteLine($"Bundle accepted: revision {bundle.Revision}, {bundle.Peers.Length} peer(s).");
        Console.Error.WriteLine();
        Console.Out.Write(ServerConfigRenderer.Render(bundle, egress));
        return true;
    }

    private static string? ReadArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
