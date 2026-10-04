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

    /// <summary>
    /// The config a probe uses to check one node. The same client material and the same wire
    /// format as a person's config - a probe that spoke differently would be testing something no
    /// client does - but aimed at the node's own address instead of the failover record, and
    /// installing no routes: the probe sends its check through the interface by binding a socket
    /// to it, so the host it runs on keeps its own routing exactly as it was.
    /// </summary>
    public static string RenderProbe(FleetRecord fleet, ClientRecord probeClient, string nodeAddress)
    {
        var network = Ipv4Network.Parse(fleet.Subnet);
        var builder = new StringBuilder();

        builder.AppendLine("[Interface]");
        builder.AppendSetting("PrivateKey", probeClient.PrivateKey);
        // A /32 and no routing table: the probe host keeps its own routes exactly as they were,
        // and no connected route for the fleet subnet appears on it.
        builder.AppendSetting("Address", $"{probeClient.Address}/32");
        builder.AppendSetting("Table", "off");

        var tunables = fleet.Obfuscation?.GetDefaults();
        builder.AppendInterfaceObfuscation(fleet.Obfuscation);
        builder.AppendTuning(tunables);
        builder.AppendClientObfuscation(tunables);

        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.AppendSetting("PublicKey", fleet.ServerPublicKey);
        builder.AppendSetting("PresharedKey", probeClient.PresharedKey);
        // Everything, as a person's config has: the check fetches a resource on the internet
        // through the node, and the replies come back from that resource's address. With
        // Table = off this installs no route, so it reroutes nothing on the probe host.
        builder.AppendSetting("AllowedIPs", "0.0.0.0/0");
        builder.AppendSetting("Endpoint", Endpoint(nodeAddress, fleet.ListenPort));
        // A keepalive is what makes the interface initiate a handshake the moment it comes up,
        // with no traffic to send. Short, because the probe waits for exactly that.
        builder.AppendSetting("PersistentKeepalive", "5");

        return builder.ToString();
    }

    private static string Endpoint(string address, int port)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return address.Contains(':', StringComparison.Ordinal) ? $"[{address}]:{portText}" : $"{address}:{portText}";
    }

    /// <summary>A config downloads as an opaque stream, never <c>text/plain</c>: Safari and
    /// Samsung Internet rewrite a download's extension to match the type they were handed, so a
    /// text/plain config lands as <c>.conf.txt</c> and the AmneziaWG app will not import it.</summary>
    public const string ContentType = "application/octet-stream";

    public static string FileName(string clientName)
    {
        var safe = new string(clientName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return (string.IsNullOrWhiteSpace(safe) ? "client" : safe) + ".conf";
    }
}
