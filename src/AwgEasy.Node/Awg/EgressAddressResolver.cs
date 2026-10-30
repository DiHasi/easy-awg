using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// The addresses the egress interface carries, and which of them the kernel is currently picking
/// as the source for outbound traffic.
/// </summary>
/// <param name="KernelDefault">
/// Null when it could not be determined, or when it is not one of <paramref name="Addresses"/> -
/// which happens when the node routes out through an interface other than the configured egress,
/// and is then not a fact about this interface at all.
/// </param>
public sealed record EgressAddressSet(string[] Addresses, string? KernelDefault);

/// <summary>
/// Reads which source addresses a node has to offer.
///
/// Only the node can answer this, which is why the panel offers an operator what was reported
/// rather than a field to type an address into: a SNAT rule naming an address the host does not
/// carry leaves that peer with no traffic at all. The same reading decides which rules the agent
/// will install, so a peer is never pinned to an address that is not there.
/// </summary>
public sealed class EgressAddressResolver(ILogger<EgressAddressResolver> logger)
{
    /// <summary>
    /// Where "out" is, for the source-address lookup. A route query sends no packets; it asks the
    /// kernel which address it would put on one, which is what unpinned peers leave with while a
    /// node has no default of its own.
    /// </summary>
    private const string OutboundProbe = "1.1.1.1";

    public async Task<EgressAddressSet?> ListAsync(string egressInterface, CancellationToken cancellationToken)
    {
        var shown = await ProcessRunner.TryRunAsync("ip", ["-4", "addr", "show", "dev", egressInterface], input: null, cancellationToken);
        if (shown is null || shown.ExitCode != 0)
        {
            // Debug, not a warning: this is asked on every status report, and a node whose
            // addresses cannot be read keeps serving traffic on the ones it has.
            logger.LogDebug("Could not read the addresses of {EgressInterface}.", egressInterface);
            return null;
        }

        var addresses = ParseAddresses(shown.Output);

        var route = await ProcessRunner.TryRunAsync("ip", ["-4", "route", "get", OutboundProbe], input: null, cancellationToken);
        var kernelDefault = route is { ExitCode: 0 } ? ParseRouteSource(route.Output) : null;

        return new EgressAddressSet(
            addresses,
            kernelDefault is not null && addresses.Contains(kernelDefault, StringComparer.Ordinal) ? kernelDefault : null);
    }

    /// <summary>
    /// Every globally scoped address in `ip -4 addr show dev ens3`, prefix dropped.
    ///
    /// Secondaries and alias labels are exactly what this is for: a provider that hands out a
    /// second address often puts it on `ens3:0` as a /32, and that address is the whole point of
    /// pinning anybody to it. Host and link scope are skipped - nothing leaves through them.
    /// </summary>
    internal static string[] ParseAddresses(string output)
    {
        var addresses = new List<string>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens is not ["inet", var value, ..] || !IsGlobal(tokens))
            {
                continue;
            }

            var bare = value.Split('/')[0];
            if (EgressAddress.TryParse(bare, out var address) && !addresses.Contains(address, StringComparer.Ordinal))
            {
                addresses.Add(address);
            }
        }

        return [.. addresses];
    }

    /// <summary>Parses "1.1.1.1 via 10.0.0.1 dev ens3 src 91.132.59.9 uid 0" into "91.132.59.9".</summary>
    internal static string? ParseRouteSource(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == "src" && EgressAddress.TryParse(tokens[i + 1], out var address))
                {
                    return address;
                }
            }
        }

        return null;
    }

    private static bool IsGlobal(string[] tokens)
    {
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i] == "scope")
            {
                return tokens[i + 1] == "global";
            }
        }

        return false;
    }
}
