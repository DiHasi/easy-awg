using System.Text.RegularExpressions;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// The settings that belong to one node rather than to the fleet, validated in one place because
/// two of the three reach a node and are applied there in a single shot.
///
/// <see cref="EgressInterface"/> and <see cref="Mtu"/> travel in the bundle, so a value that `awg`
/// or `iptables` refuses does not produce a complaint in the panel - it produces a node whose
/// interface does not come up at all, holding a configuration it cannot apply, while the panel
/// shows it as merely out of sync. That is why the interface name is checked against what Linux
/// will actually accept rather than merely for being non-empty.
///
/// <see cref="EndpointHost"/> reaches no node: it is a note on the node row for an operator who
/// gives a node a record of its own. It is still checked, because a label nobody validates ends
/// up pasted into a config by hand.
/// </summary>
public sealed record NodeOverrides(string? EndpointHost, string? EgressInterface, int? Mtu)
{
    /// <summary>No overrides: the fleet value applies and the agent detects its own egress.</summary>
    public static readonly NodeOverrides None = new(null, null, null);

    /// <summary>
    /// A Linux interface name: at most 15 characters (IFNAMSIZ less the terminator), and nothing
    /// that could be read as another shell word or another iptables argument once the agent builds
    /// its rules from it.
    /// </summary>
    private static readonly Regex InterfaceName = new(@"^[A-Za-z0-9_.-]{1,15}$", RegexOptions.CultureInvariant);

    /// <summary>A hostname or address, loose enough for IPv6 literals and punycode, strict enough
    /// to refuse a sentence or a URL.</summary>
    private static readonly Regex Host = new(@"^[A-Za-z0-9._:\[\]-]{1,253}$", RegexOptions.CultureInvariant);

    public bool ChangesBundle(NodeOverrides other)
        => !string.Equals(EgressInterface, other.EgressInterface, StringComparison.Ordinal) || Mtu != other.Mtu;

    /// <summary>
    /// Reads what a request carried. Blank is not a value: an empty field in a form means "leave
    /// it to the fleet", and storing an empty string would hand the agent an egress interface
    /// named nothing.
    /// </summary>
    public static bool TryRead(string? endpointHost, string? egressInterface, int? mtu, out NodeOverrides overrides, out ApiError error)
    {
        overrides = None;
        error = default!;

        var host = Blank(endpointHost);
        var egress = Blank(egressInterface);

        if (host is not null && !Host.IsMatch(host))
        {
            error = new ApiError("node_endpoint_host_invalid", "Endpoint host must be a hostname or an address.");
            return false;
        }

        if (egress is not null && !InterfaceName.IsMatch(egress))
        {
            error = new ApiError(
                "node_egress_interface_invalid",
                "Egress interface must be a Linux interface name: up to 15 characters, letters, digits, dot, dash or underscore.");

            return false;
        }

        if (mtu is { } value && (value < FleetService.DefaultTunnelMtu || value > FleetService.MaxTunnelMtu))
        {
            error = new ApiError(
                "invalid_mtu",
                $"MTU must be between {FleetService.DefaultTunnelMtu} and {FleetService.MaxTunnelMtu}.");

            return false;
        }

        overrides = new NodeOverrides(host, egress, mtu);
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
