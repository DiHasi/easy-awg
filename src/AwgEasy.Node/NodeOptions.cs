using System.Globalization;

namespace AwgEasy.Node;

/// <param name="ControlUrl">Base URL of the control plane. Null runs the agent offline, applying only <paramref name="BundleFile"/>.</param>
/// <param name="BundleFile">Optional local bundle, for bootstrap and for testing a node without a control plane.</param>
public sealed record NodeOptions(
    string? ControlUrl,
    string? EnrollmentToken,
    string? BundleFile,
    string StatePath,
    string InterfaceName,
    string InterfaceConfigPath,
    string? EgressInterface,
    TimeSpan PollInterval,
    string HealthUrl)
{
    public string AgentIdentityPath => Path.Combine(StatePath, "agent.json");

    public string CachedBundlePath => Path.Combine(StatePath, "bundle.json");

    public bool IsOffline => string.IsNullOrWhiteSpace(ControlUrl);

    public static NodeOptions FromEnvironment()
        => new(
            Trimmed("AWG_CONTROL_URL"),
            Trimmed("AWG_ENROLLMENT_TOKEN"),
            Trimmed("AWG_BUNDLE_FILE"),
            Trimmed("AWG_NODE_STATE_PATH") ?? "/etc/awg-node",
            Trimmed("AWG_INTERFACE") ?? "awg0",
            Trimmed("AWG_INTERFACE_CONFIG_PATH") ?? "/etc/amnezia/amneziawg/awg0.conf",
            Trimmed("AWG_EGRESS_INTERFACE"),
            TimeSpan.FromSeconds(ReadInt("AWG_POLL_INTERVAL_SECONDS", 20)),
            Trimmed("AWG_HEALTH_URL") ?? "http://127.0.0.1:8081");

    private static string? Trimmed(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReadInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
