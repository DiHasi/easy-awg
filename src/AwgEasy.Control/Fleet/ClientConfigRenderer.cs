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
        builder.Append("PrivateKey = ").AppendLine(client.PrivateKey);
        builder.Append("Address = ").Append(client.Address).Append('/')
            .AppendLine(network.PrefixLength.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(fleet.ClientDns))
        {
            builder.Append("DNS = ").AppendLine(fleet.ClientDns);
        }

        // A client config carries both halves: the interface-side values that must match the
        // server (S1-S4, H1-H4) and the client-side jitter/packet knobs.
        AppendServerObfuscation(builder, fleet.Obfuscation);
        AppendClientObfuscation(builder, fleet.Obfuscation?.Merge(client.Obfuscation) ?? client.Obfuscation);

        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.Append("PublicKey = ").AppendLine(fleet.ServerPublicKey);
        builder.Append("PresharedKey = ").AppendLine(client.PresharedKey);
        builder.Append("AllowedIPs = ").AppendLine(fleet.ClientAllowedIps);
        builder.Append("Endpoint = ").Append(endpointHost).Append(':')
            .AppendLine(fleet.ListenPort.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("PersistentKeepalive = 25");

        return builder.ToString();
    }

    public static string FileName(string clientName)
    {
        var safe = new string(clientName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return (string.IsNullOrWhiteSpace(safe) ? "client" : safe) + ".conf";
    }

    private static void AppendServerObfuscation(StringBuilder builder, ServerObfuscationProfile? obfuscation)
    {
        if (obfuscation is null)
        {
            return;
        }

        Append(builder, "S1", obfuscation.S1);
        Append(builder, "S2", obfuscation.S2);
        Append(builder, "S3", obfuscation.S3);
        Append(builder, "S4", obfuscation.S4);
        Append(builder, "H1", obfuscation.H1);
        Append(builder, "H2", obfuscation.H2);
        Append(builder, "H3", obfuscation.H3);
        Append(builder, "H4", obfuscation.H4);
    }

    private static void AppendClientObfuscation(StringBuilder builder, ClientObfuscationOverrides? obfuscation)
    {
        if (obfuscation is null)
        {
            return;
        }

        Append(builder, "Jc", obfuscation.Jc);
        Append(builder, "Jmin", obfuscation.Jmin);
        Append(builder, "Jmax", obfuscation.Jmax);
        Append(builder, "I1", obfuscation.I1);
        Append(builder, "I2", obfuscation.I2);
        Append(builder, "I3", obfuscation.I3);
        Append(builder, "I4", obfuscation.I4);
        Append(builder, "I5", obfuscation.I5);
    }

    private static void Append(StringBuilder builder, string key, int? value)
    {
        if (value.HasValue)
        {
            builder.Append(key).Append(" = ").AppendLine(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(key).Append(" = ").AppendLine(value);
        }
    }
}
