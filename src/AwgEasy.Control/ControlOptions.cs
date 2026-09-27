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
    string? LegacyStateImportPath,
    DnsFailoverOptions Dns)
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
            Value("AWG_IMPORT_LEGACY_STATE"),
            DnsFailoverOptions.FromEnvironment(Value, ReadInt));

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

/// <summary>
/// How the failover record is moved. Credentials stay in the environment rather than the database:
/// a token that can edit a DNS zone should not be readable through the panel, and nothing here is
/// per-fleet state worth backing up.
/// </summary>
/// <param name="RecordName">The record to move. Null means the host clients already connect to.</param>
/// <param name="Ttl">
/// Deliberately low. Failover is only as fast as the TTL resolvers were handed before the switch,
/// so a long one leaves clients on a dead node for exactly that long.
/// </param>
public sealed record DnsFailoverOptions(
    string? RecordName,
    int Ttl,
    string? CloudflareApiToken,
    string? CloudflareZoneId)
{
    public bool CloudflareConfigured
        => !string.IsNullOrWhiteSpace(CloudflareApiToken) && !string.IsNullOrWhiteSpace(CloudflareZoneId);

    public static DnsFailoverOptions FromEnvironment(Func<string, string?> value, Func<string, int, int> readInt)
        => new(
            value("AWG_DNS_RECORD_NAME"),
            readInt("AWG_DNS_TTL", 60),
            value("AWG_CLOUDFLARE_API_TOKEN"),
            value("AWG_CLOUDFLARE_ZONE_ID"));
}
