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

        // A client config carries both halves: the wire-format values that must match the server,
        // and everything that may differ per client, with per-client overrides winning.
        var tunables = fleet.Obfuscation?.Merge(client.Obfuscation) ?? client.Obfuscation;
        builder.AppendInterfaceObfuscation(fleet.Obfuscation);
        builder.AppendTuning(tunables);
        builder.AppendClientObfuscation(tunables);

        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.AppendSetting("PublicKey", fleet.ServerPublicKey);
        builder.AppendSetting("PresharedKey", client.PresharedKey);
        builder.AppendSetting("AllowedIPs", fleet.ClientAllowedIps);
        builder.AppendSetting("Endpoint", $"{endpointHost}:{fleet.ListenPort.ToString(CultureInfo.InvariantCulture)}");
        // 3.x accepts a range here. Keeping every client on a fixed 25s makes the fleet's
        // keepalives line up into one recognizable heartbeat, so a range is worth configuring -
        // but the stock value stays the fallback so an unconfigured fleet still behaves.
        builder.AppendSetting("PersistentKeepalive", tunables?.PersistentKeepalive ?? "25");

        return builder.ToString();
    }

    public static string FileName(string clientName)
    {
        var safe = new string(clientName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return (string.IsNullOrWhiteSpace(safe) ? "client" : safe) + ".conf";
    }
}
