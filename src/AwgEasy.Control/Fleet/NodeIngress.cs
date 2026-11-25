using System.Text.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>The externally discovered address plus public IPv4 addresses the host carries.</summary>
public static class NodeIngress
{
    public static string[] Addresses(NodeRecord node)
        => (string.IsNullOrWhiteSpace(node.IngressReport) ? []
                : JsonSerializer.Deserialize(node.IngressReport, ContractsJsonContext.Default.StringArray) ?? [])
            .Where(address => EgressAddress.TryParse(address, out _) && PublicIpAddress.TryParse(address, out _))
            // Explicit AWG_PUBLIC_IP is allowed to name a private address in a private deployment.
            .Prepend(node.PublicIp ?? string.Empty)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
