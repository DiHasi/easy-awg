using System.Globalization;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Renders downloadable client configs. This stays in the control plane, not the node: it needs
/// client private keys, which never leave here.
/// </summary>
public static class ClientConfigRenderer
{
    public static string Render(FleetRecord fleet, ClientRecord client, string endpointHost)
    {
        var network = Ipv4Network.Parse(fleet.Subnet);
        var builder = new StringBuilder();

        builder.AppendLine("[Interface]");
        builder.AppendSetting("PrivateKey", client.PrivateKey);
        builder.AppendSetting("Address", $"{client.Address}/{network.PrefixLength.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendSetting("DNS", fleet.ClientDns);

        // A client config carries both halves: the interface-side values that must match the
        // server, and the client-side jitter knobs, with per-client overrides winning.
        builder.AppendInterfaceObfuscation(fleet.Obfuscation);
        builder.AppendClientObfuscation(fleet.Obfuscation?.Merge(client.Obfuscation) ?? client.Obfuscation);

        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.AppendSetting("PublicKey", fleet.ServerPublicKey);
        builder.AppendSetting("PresharedKey", client.PresharedKey);
        builder.AppendSetting("AllowedIPs", fleet.ClientAllowedIps);
        builder.AppendSetting("Endpoint", $"{endpointHost}:{fleet.ListenPort.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendSetting("PersistentKeepalive", 25);

        return builder.ToString();
    }

    public static string FileName(string clientName)
    {
        var safe = new string(clientName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return (string.IsNullOrWhiteSpace(safe) ? "client" : safe) + ".conf";
    }
}
