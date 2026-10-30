using System.Net;
using System.Net.Sockets;

namespace AwgEasy.Contracts;

/// <summary>
/// Decides whether an address is usable as the source a node's traffic leaves with.
///
/// Deliberately not <see cref="PublicIpAddress"/>, which the panel uses for the address clients
/// connect to and which refuses anything the internet cannot reach. A source address is the other
/// direction: behind a provider's NAT the egress interface legitimately holds an RFC 1918 address
/// and the public one lives upstream, so refusing private ranges here would refuse the very hosts
/// that have no choice. What is refused is an address no packet could carry out - the wildcard,
/// loopback, multicast, broadcast - and IPv6, which nothing in the tunnel speaks yet.
///
/// Whether the node actually holds the address is a different question, and not one that can be
/// answered here: only the node knows, which is why it reports its addresses and the panel offers
/// what was reported.
/// </summary>
public static class EgressAddress
{
    public static bool TryParse(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        if (octets[0] is 0 or 127 or >= 224 || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }
}
