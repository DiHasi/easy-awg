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
    DnsFailoverOptions Dns,
    FailoverOptions Failover,
    NotificationOptions Notifications,
    UsageOptions Usage)
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
            DnsFailoverOptions.FromEnvironment(Value, ReadInt),
            FailoverOptions.FromEnvironment(Value, ReadInt),
            NotificationOptions.FromEnvironment(Value),
            UsageOptions.FromEnvironment());

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

/// <summary>
/// When automatic failover acts. Whether it acts at all is not here: that is a switch in the
/// panel, stored with the fleet, because it is the thing an operator turns off in a hurry.
/// </summary>
/// <param name="CheckInterval">How often the panel re-evaluates the fleet.</param>
/// <param name="Grace">
/// How long the active node has to be failing before traffic moves. A switch sends every client
/// through a reconnect, so a blip shorter than this is cheaper to sit out than to fix.
/// </param>
/// <param name="Cooldown">
/// The minimum time between an activation - manual or automatic - and the next automatic one.
/// Without it two half-broken nodes would pass the traffic back and forth every grace period.
/// </param>
/// <param name="AgentStaleAfter">A node whose last status report is older than this is silent.</param>
/// <param name="ProbeStaleAfter">A probe result older than this is no longer evidence of anything.</param>
/// <param name="ProbeInterval">How often probes are asked to run a round.</param>
/// <param name="ProbeHandshakeTimeout">How long a probe waits for one node to answer.</param>
/// <param name="ProbeCheckUrls">
/// What a probe fetches through the tunnel after the handshake. Several, and any one answering is
/// enough, so one site being down does not read as every node being blocked.
/// </param>
/// <param name="ProbeTrafficTimeout">How long each of those has to answer.</param>
public sealed record FailoverOptions(
    TimeSpan CheckInterval,
    TimeSpan Grace,
    TimeSpan Cooldown,
    TimeSpan AgentStaleAfter,
    TimeSpan ProbeStaleAfter,
    TimeSpan ProbeInterval,
    TimeSpan ProbeHandshakeTimeout,
    IReadOnlyList<string> ProbeCheckUrls,
    TimeSpan ProbeTrafficTimeout)
{
    /// <summary>
    /// Plain HTTP, and an address literal first: nothing to resolve and no certificate to trust,
    /// so the only thing that can stop the answer is the tunnel. Any status counts.
    /// </summary>
    public static readonly string[] DefaultProbeCheckUrls =
    [
        "http://1.1.1.1/cdn-cgi/trace",
        "http://connectivitycheck.gstatic.com/generate_204"
    ];

    public static FailoverOptions FromEnvironment(Func<string, string?> value, Func<string, int, int> readInt)
        => new(
            TimeSpan.FromSeconds(readInt("AWG_FAILOVER_CHECK_SECONDS", 30)),
            TimeSpan.FromSeconds(readInt("AWG_FAILOVER_GRACE_SECONDS", 120)),
            TimeSpan.FromMinutes(readInt("AWG_FAILOVER_COOLDOWN_MINUTES", 15)),
            // Agents report every 20 seconds by default, so this is several missed reports.
            TimeSpan.FromSeconds(readInt("AWG_NODE_STALE_SECONDS", 90)),
            TimeSpan.FromSeconds(readInt("AWG_PROBE_STALE_SECONDS", 300)),
            TimeSpan.FromSeconds(readInt("AWG_PROBE_INTERVAL_SECONDS", 60)),
            // WireGuard retries a handshake every five seconds; this is three attempts.
            TimeSpan.FromSeconds(readInt("AWG_PROBE_HANDSHAKE_TIMEOUT_SECONDS", 15)),
            ReadUrls(value("AWG_PROBE_CHECK_URLS")),
            TimeSpan.FromSeconds(readInt("AWG_PROBE_TRAFFIC_TIMEOUT_SECONDS", 8)));

    /// <summary>Unset uses the defaults. "none" switches the traffic check off, leaving the handshake alone.</summary>
    private static string[] ReadUrls(string? raw)
        => raw is null
            ? DefaultProbeCheckUrls
            : string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase)
                ? []
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Where the panel says that something happened without being asked. Credentials stay in the
/// environment for the same reason the DNS token does: nothing that can post as the operator
/// should be readable through the panel.
/// </summary>
public sealed record NotificationOptions(
    string? WebhookUrl,
    string? TelegramBotToken,
    string? TelegramChatId)
{
    public bool TelegramConfigured
        => !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramChatId);

    public static NotificationOptions FromEnvironment(Func<string, string?> value)
        => new(
            value("AWG_NOTIFY_WEBHOOK_URL"),
            value("AWG_NOTIFY_TELEGRAM_BOT_TOKEN"),
            value("AWG_NOTIFY_TELEGRAM_CHAT_ID"));
}

/// <summary>
/// How long the panel keeps a per-peer traffic history.
///
/// One knob, because it is one decision: an hourly record of what each person moved is far more
/// than the lifetime counter beside it, and an operator who does not want to hold it should be
/// able to say so in one place. Zero means the panel never writes a row - the live counters keep
/// working, because they come from the nodes and not from here.
/// </summary>
/// <param name="RetentionDays">Days of history kept. Zero switches recording off entirely.</param>
public sealed record UsageOptions(int RetentionDays)
{
    /// <summary>Long enough to answer "what did this month cost", short enough to stay a log and not an archive.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>A ceiling, not a target: past a year this stops being a log and becomes an archive.</summary>
    public const int MaxRetentionDays = 365;

    public bool Records => RetentionDays > 0;

    /// <summary>
    /// Read here rather than through <c>ControlOptions.ReadInt</c>: that helper rejects zero as a
    /// mistyped value, and zero is the one setting here that has to mean something.
    /// </summary>
    public static UsageOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("AWG_USAGE_RETENTION_DAYS");
        return new(
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days >= 0
                ? Math.Min(days, MaxRetentionDays)
                : DefaultRetentionDays);
    }
}
