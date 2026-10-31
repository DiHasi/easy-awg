using System.Globalization;
using System.Net;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// The two settings a client config carries that are nobody's business but that client's: what it
/// routes into the tunnel, and which resolver it uses.
///
/// Both are render-time only, and that is the whole reason they can exist per peer at all. A
/// client's <c>AllowedIPs</c> says what that device sends through the tunnel; the node's own
/// <c>AllowedIPs</c> for the same peer is that peer's tunnel address, which is what the node will
/// accept from it, and the two are unrelated. So nothing here reaches a node, nothing enters a
/// bundle, and nothing bumps the revision - the same place in the model as grouping, except that
/// this does change the bytes of the config a person is handed, so changing it means handing that
/// person their config again.
///
/// Validated here rather than at the form, for the reason the obfuscation profile is: a config
/// with a value <c>awg-quick</c> refuses does not fail halfway, it fails to import at all - and
/// the person holding it is the one who finds out, usually without being able to say why.
/// </summary>
public static class ClientRouting
{
    /// <summary>What the operator is told when a route list will not parse.</summary>
    public const string AllowedIpsRule =
        "AllowedIPs is a comma-separated list of CIDR ranges, for example '0.0.0.0/0, ::/0' or "
        + "'10.0.0.0/8, 192.168.0.0/16'. Leave it empty to use the fleet value.";

    /// <summary>And when a resolver list will not.</summary>
    public const string DnsRule =
        "DNS is a comma-separated list of resolver addresses, and may end with search domains, for "
        + "example '1.1.1.1, 1.0.0.1'. Leave it empty to use the fleet value.";

    /// <summary>
    /// Reads a route list, or null when the field is blank - which means the fleet value applies
    /// and never an empty string: a config with <c>AllowedIPs =</c> and nothing after it routes
    /// nothing at all, which is a tunnel that connects and carries no traffic.
    /// </summary>
    public static bool TryReadAllowedIps(string? value, out string? normalized, out ApiError error)
    {
        normalized = null;
        error = ApiError.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var entries = Split(value);
        if (entries.Length == 0)
        {
            error = new ApiError("client_allowed_ips_invalid", AllowedIpsRule);
            return false;
        }

        foreach (var entry in entries)
        {
            // Both families: the fleet default carries ::/0, and a peer narrowing its routes has
            // every reason to keep doing so.
            if (!IPNetwork.TryParse(entry, out _))
            {
                error = new ApiError("client_allowed_ips_invalid", AllowedIpsRule);
                return false;
            }
        }

        normalized = string.Join(", ", entries);
        return true;
    }

    /// <summary>
    /// Reads a resolver list. Addresses, and the search domains <c>wg-quick</c> also accepts in
    /// this field - a deployment with an internal zone has a real use for them, and refusing one
    /// would be the panel being stricter than the tool it writes for.
    /// </summary>
    public static bool TryReadDns(string? value, out string? normalized, out ApiError error)
    {
        normalized = null;
        error = ApiError.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var entries = Split(value);
        if (entries.Length == 0)
        {
            error = new ApiError("client_dns_invalid", DnsRule);
            return false;
        }

        foreach (var entry in entries)
        {
            if (!IPAddress.TryParse(entry, out _) && !IsHostname(entry))
            {
                error = new ApiError("client_dns_invalid", DnsRule);
                return false;
            }
        }

        normalized = string.Join(", ", entries);
        return true;
    }

    private static string[] Split(string value)
        => [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// A search domain, as loosely as a resolver list has any business being checked: letters,
    /// digits, hyphens and dots. Strict enough to catch the thing this is really here for - a
    /// line of prose pasted into the field - and no stricter, because the panel is in no position
    /// to know what somebody's internal zone is called.
    /// </summary>
    private static bool IsHostname(string entry)
        => entry.Length <= 253
            && entry.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')
            && char.IsAsciiLetterOrDigit(entry[0])
            && entry.Contains('.', StringComparison.Ordinal);

    /// <summary>What a config will actually say, for a page that wants to show it without
    /// rendering the whole file: the peer's own value where it has one, the fleet's otherwise.
    /// </summary>
    public static string Describe(string? peerValue, string? fleetValue)
        => peerValue ?? fleetValue ?? string.Empty;

    /// <summary>Used by the event log, which reads better naming the fleet than saying "null".</summary>
    public static string DescribeChange(string? allowedIps, string? dns)
    {
        var parts = new List<string>
        {
            $"AllowedIPs {(allowedIps is null ? "from the fleet" : allowedIps)}",
            $"DNS {(dns is null ? "from the fleet" : dns)}"
        };

        return string.Join(", ", parts).ToString(CultureInfo.InvariantCulture);
    }
}
