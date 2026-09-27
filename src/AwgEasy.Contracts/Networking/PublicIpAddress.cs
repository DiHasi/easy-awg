using System.Net;
using System.Net.Sockets;

namespace AwgEasy.Contracts;

/// <summary>
/// Decides whether an address is usable as a node's public endpoint.
///
/// A node discovers its own address by asking an outside service, and the control plane writes
/// that value into the DNS record every client follows. An address that cannot be reached from
/// the internet - a private one behind the provider's NAT, a link-local one, a lookup that came
/// back with something else entirely - would produce a record that resolves to nothing, so the
/// node refuses to report one.
/// </summary>
public static class PublicIpAddress
{
    /// <summary>Parses and normalizes an address, rejecting anything not reachable from the internet.</summary>
    public static bool TryParse(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var address) || !IsPubliclyRoutable(address))
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false
        };
    }

    /// <summary>
    /// Covers what an echo service actually answers with when something is wrong: an address from
    /// the provider's private range, from carrier NAT, or from a failed DHCP lease. The documentation
    /// ranges are deliberately left alone - they never come back from a real lookup, and excluding
    /// them would only make them unusable as example values.
    /// </summary>
    private static bool IsPublicV4(byte[] octets)
        => octets[0] switch
        {
            0 => false,                                          // this network
            10 => false,                                         // RFC 1918
            127 => false,                                        // loopback
            100 => octets[1] is < 64 or > 127,                    // RFC 6598 carrier NAT
            169 => octets[1] != 254,                             // link-local
            172 => octets[1] is < 16 or > 31,                     // RFC 1918
            192 => octets[1] != 168,                             // RFC 1918
            >= 224 => false,                                      // multicast, reserved, broadcast
            _ => true
        };

    private static bool IsPublicV6(IPAddress address)
    {
        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        // fc00::/7 unique local: the v6 equivalent of RFC 1918, and not flagged by the BCL.
        return (address.GetAddressBytes()[0] & 0xFE) != 0xFC;
    }
}
