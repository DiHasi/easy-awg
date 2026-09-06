using System.Globalization;

namespace AwgEasy.Control;

public sealed record ControlOptions(
    string DatabasePath,
    string DefaultSubnet,
    int DefaultListenPort,
    string DefaultClientAllowedIps,
    string? DefaultClientDns,
    string DefaultEndpointHost,
    TimeSpan BundleLifetime,
    string? BootstrapAdminUser,
    string? BootstrapAdminPassword,
    string? LegacyStateImportPath)
{
    public static ControlOptions FromEnvironment()
        => new(
            Value("AWG_CONTROL_DB") ?? "/etc/awg-control/control.db",
            Value("AWG_SUBNET") ?? "10.8.0.0/24",
            ReadInt("AWG_PORT", 51820),
            Value("AWG_CLIENT_ALLOWED_IPS") ?? "0.0.0.0/0, ::/0",
            Value("AWG_CLIENT_DNS"),
            Value("AWG_ENDPOINT_HOST") ?? string.Empty,
            // Short-lived so a captured bundle cannot be replayed at a node much later.
            TimeSpan.FromMinutes(ReadInt("AWG_BUNDLE_LIFETIME_MINUTES", 15)),
            Value("AWG_ADMIN_USER"),
            Value("AWG_ADMIN_PASSWORD"),
            Value("AWG_IMPORT_LEGACY_STATE"));

    /// <summary>
    /// Settings that have no safe default. AWG_ENDPOINT_HOST in particular is written into every
    /// client config: a wrong value produces configs that look fine and connect to nothing, so
    /// refusing to start is better than guessing.
    /// </summary>
    public string? Validate()
        => string.IsNullOrWhiteSpace(DefaultEndpointHost)
            ? "AWG_ENDPOINT_HOST is not set. It is the public hostname or IP clients connect to, "
              + "and it goes into every client config. Set it and restart."
            : null;

    private static string? Value(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReadInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
