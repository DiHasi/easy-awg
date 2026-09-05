using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AwgEasy.Contracts;

public sealed record Ipv4Network(uint NetworkAddress, int PrefixLength)
{
    public uint UsableHosts => PrefixLength >= 31 ? 0u : (1u << (32 - PrefixLength)) - 1u;

    public static Ipv4Network Parse(string value)
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"Invalid IPv4 CIDR subnet: {value}");
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix) || prefix is < 16 or > 30)
        {
            throw new InvalidOperationException("AWG_SUBNET prefix must be between /16 and /30.");
        }

        var raw = IpToUInt(ip);
        var mask = uint.MaxValue << (32 - prefix);
        return new Ipv4Network(raw & mask, prefix);
    }

    public static bool TryParse(string value, out Ipv4Network network, out ApiError error)
    {
        try
        {
            network = Parse(value);
            error = ApiError.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            network = default!;
            error = new ApiError("invalid_subnet", exception.Message);
            return false;
        }
    }

    /// <summary>Address of the tunnel gateway (first usable host), used by the node interface.</summary>
    public string GatewayAddress => GetAddress(1);

    public string GetAddress(uint hostOffset) => UIntToIp(NetworkAddress + hostOffset).ToString();

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = IpToUInt(address);
        var mask = uint.MaxValue << (32 - PrefixLength);
        return (value & mask) == NetworkAddress;
    }

    private static uint IpToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UIntToIp(uint value)
        => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
