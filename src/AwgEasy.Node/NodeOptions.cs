using System.Globalization;

namespace AwgEasy.Node;

/// <param name="ControlUrl">Base URL of the control plane. Null runs the agent offline, applying only <paramref name="BundleFile"/>.</param>
/// <param name="BundleFile">Optional local bundle, for bootstrap and for testing a node without a control plane.</param>
/// <param name="PublicIpOverride">Stated instead of discovered. Reported verbatim, checks and all skipped.</param>
/// <param name="PublicIpUrls">Plain-text echo services, tried in order. Empty switches discovery off.</param>
public sealed record NodeOptions(
    string? ControlUrl,
    string? EnrollmentToken,
    string? BundleFile,
    string StatePath,
    string InterfaceName,
    string InterfaceConfigPath,
    string? EgressInterface,
    TimeSpan PollInterval,
    string HealthUrl,
    string? PublicIpOverride,
    IReadOnlyList<string> PublicIpUrls,
    TimeSpan PublicIpRefreshInterval)
{
    /// <summary>Kept short: the lookup rides inside a reconcile cycle that must not stall on it.</summary>
    public TimeSpan PublicIpTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tried in order, and deliberately more than one: these are plain-text endpoints that answer
    /// with nothing but the caller's address, and a node is exactly the kind of host where one of
    /// them turns out to be unreachable.
    /// </summary>
    public static readonly string[] DefaultPublicIpUrls =
    [
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com"
    ];

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
            Trimmed("AWG_HEALTH_URL") ?? "http://127.0.0.1:8081",
            Trimmed("AWG_PUBLIC_IP"),
            ReadUrls("AWG_PUBLIC_IP_URLS"),
            TimeSpan.FromMinutes(ReadInt("AWG_PUBLIC_IP_REFRESH_MINUTES", 10)));

    /// <summary>An explicitly empty value switches discovery off; unset falls back to the defaults.</summary>
    private static IReadOnlyList<string> ReadUrls(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw is null)
        {
            return DefaultPublicIpUrls;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

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
