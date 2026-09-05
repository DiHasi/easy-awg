using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Allocates tunnel addresses.
///
/// This moved out of the node and into the control plane on purpose. Every node runs the same
/// fleet identity, so a client must work on any of them - which means addresses have to be
/// unique across the whole fleet, not just on the node that happened to create the client.
/// </summary>
public static class AddressAllocator
{
    /// <summary>Host .1 is the tunnel gateway held by every node, so clients start at .2.</summary>
    private const uint FirstClientHost = 2;

    public static bool TryAllocate(string subnet, IReadOnlySet<string> used, out string address, out ApiError error)
    {
        address = string.Empty;
        if (!Ipv4Network.TryParse(subnet, out var network, out error))
        {
            return false;
        }

        for (var host = FirstClientHost; host < network.UsableHosts; host++)
        {
            var candidate = network.GetAddress(host);
            if (!used.Contains(candidate))
            {
                address = candidate;
                error = ApiError.Empty;
                return true;
            }
        }

        error = new ApiError("subnet_exhausted", "No free client addresses left in the configured subnet.");
        return false;
    }
}
