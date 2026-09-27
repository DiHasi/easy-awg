using System.Net;
using System.Net.Sockets;

namespace AwgEasy.Control;

/// <summary>
/// Looks up what a name currently resolves to. Behind an interface so the panel's view of DNS is
/// something the test suite states rather than something it inherits from the machine it runs on.
/// </summary>
public interface IHostAddressResolver
{
    Task<string[]> ResolveAsync(string name, CancellationToken cancellationToken);
}

public sealed class SystemHostAddressResolver(ILogger<SystemHostAddressResolver> logger) : IHostAddressResolver
{
    public async Task<string[]> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        // A literal address never had a lookup to do, and asking for one would only produce a
        // confusing failure on a name that is not a name.
        if (IPAddress.TryParse(name, out var literal))
        {
            return [literal.ToString()];
        }

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(name, cancellationToken);
            return [.. addresses.Select(address => address.ToString())];
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            logger.LogDebug(exception, "Could not resolve {Name} from the panel.", name);
            return [];
        }
    }
}
