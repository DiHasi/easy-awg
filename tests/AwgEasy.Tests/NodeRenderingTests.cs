using AwgEasy.Contracts;
using AwgEasy.Node;

namespace AwgEasy.Tests;

public class ServerConfigRendererTests
{
    private static DesiredStateBundle Bundle(
        ServerObfuscationProfile? obfuscation = null,
        BundlePeer[]? peers = null,
        int? mtu = null,
        string subnet = "10.8.0.0/24")
        => new(
            DesiredStateBundle.CurrentSchemaVersion,
            7,
            "node-a",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(10),
            new FleetIdentity(1, "SERVER_PRIVATE", "SERVER_PUBLIC"),
            new NetworkProfile(subnet, 51820),
            obfuscation,
            new NodeSettings("awg0", null, mtu),
            peers ?? [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2")]);

    [Fact]
    public void Uses_the_first_host_of_the_subnet_as_the_interface_address()
    {
        var config = ServerConfigRenderer.Render(Bundle(subnet: "10.9.0.0/22"), "eth0");
        Assert.Contains("Address = 10.9.0.1/22", config);
    }

    // The single-server version hardcoded eth0, which silently broke NAT on hosts using ens3 etc.
    [Fact]
    public void Masquerades_through_the_resolved_egress_interface()
    {
        var config = ServerConfigRenderer.Render(Bundle(), "ens3");
        Assert.Contains("POSTROUTING -o ens3 -j MASQUERADE", config);
        Assert.DoesNotContain("eth0", config);
    }

    [Fact]
    public void Writes_interface_side_obfuscation_only()
    {
        var profile = new ServerObfuscationProfile
        {
            S1 = 15,
            H1 = "1234567891",
            // Client-side knobs belong in client configs, never in the node interface config.
            DefaultJc = 4,
            DefaultI1 = "<b 0xf00d>"
        };

        var config = ServerConfigRenderer.Render(Bundle(profile), "eth0");
        Assert.Contains("S1 = 15", config);
        Assert.Contains("H1 = 1234567891", config);
        Assert.DoesNotContain("Jc", config);
        Assert.DoesNotContain("I1", config);
    }

    [Fact]
    public void Renders_one_peer_block_per_peer_with_a_slash_32_allowed_ip()
    {
        BundlePeer[] peers =
        [
            new("PEER_ONE", "PSK_ONE", "10.8.0.2"),
            new("PEER_TWO", "PSK_TWO", "10.8.0.3")
        ];

        var config = ServerConfigRenderer.Render(Bundle(peers: peers), "eth0");
        Assert.Equal(2, config.Split("[Peer]").Length - 1);
        Assert.Contains("AllowedIPs = 10.8.0.2/32", config);
        Assert.Contains("AllowedIPs = 10.8.0.3/32", config);
    }

    [Fact]
    public void Omits_mtu_when_it_is_not_set()
    {
        Assert.DoesNotContain("MTU", ServerConfigRenderer.Render(Bundle(), "eth0"));
        Assert.Contains("MTU = 1420", ServerConfigRenderer.Render(Bundle(mtu: 1420), "eth0"));
    }
}

public class EgressInterfaceResolverTests
{
    [Theory]
    [InlineData("default via 10.0.0.1 dev ens3 proto dhcp metric 100", "ens3")]
    [InlineData("default via 172.31.1.1 dev eth0 proto static", "eth0")]
    [InlineData("default via 192.168.0.1 dev enp1s0 proto dhcp src 192.168.0.10 metric 100", "enp1s0")]
    public void Parses_the_interface_out_of_the_default_route(string routeOutput, string expected)
    {
        Assert.Equal(expected, EgressInterfaceResolver.ParseDefaultRouteInterface(routeOutput));
    }

    [Fact]
    public void Returns_null_when_there_is_no_default_route()
    {
        Assert.Null(EgressInterfaceResolver.ParseDefaultRouteInterface(string.Empty));
    }
}

public class AwgDumpParsingTests
{
    // `awg show <iface> dump`: first line is the interface, peers follow as
    // publickey, presharedkey, endpoint, allowedips, latest-handshake, rx, tx, keepalive.
    private const string Dump =
        "SERVER_PRIVATE\tSERVER_PUBLIC\t51820\toff\n" +
        "PEER_ONE\tPSK_ONE\t203.0.113.5:1234\t10.8.0.2/32\t1767225600\t1024\t2048\t25\n" +
        "PEER_TWO\tPSK_TWO\t(none)\t10.8.0.3/32\t0\t0\t0\toff\n";

    [Fact]
    public void Skips_the_interface_line_and_reads_every_peer()
    {
        var peers = AwgRuntime.ParseDump(Dump);
        Assert.Equal(2, peers.Length);
        Assert.Equal("PEER_ONE", peers[0].PublicKey);
        Assert.Equal(1024, peers[0].ReceivedBytes);
        Assert.Equal(2048, peers[0].TransmittedBytes);
    }

    [Fact]
    public void Reports_a_peer_that_never_handshook_as_null_rather_than_the_unix_epoch()
    {
        var peers = AwgRuntime.ParseDump(Dump);
        Assert.NotNull(peers[0].LatestHandshakeAt);
        Assert.Null(peers[1].LatestHandshakeAt);
    }

    [Fact]
    public void Ignores_malformed_lines()
    {
        Assert.Empty(AwgRuntime.ParseDump("iface-line\nnot\tenough\tcolumns\n"));
    }
}
