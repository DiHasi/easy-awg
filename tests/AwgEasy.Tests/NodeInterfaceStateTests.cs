using AwgEasy.Contracts;
using AwgEasy.Node;

namespace AwgEasy.Tests;

public class AwgLinkStateParsingTests
{
    private const string Output =
        "4: awg0: <POINTOPOINT,NOARP,UP,LOWER_UP> mtu 1420 qdisc noqueue state UNKNOWN group default qlen 1000\n" +
        "    link/none\n" +
        "    inet 10.8.0.1/24 scope global awg0\n" +
        "       valid_lft forever preferred_lft forever\n";

    [Fact]
    public void Reads_the_address_and_mtu_the_interface_actually_carries()
    {
        var state = AwgRuntime.ParseLinkState(Output);

        Assert.Equal("10.8.0.1/24", state.Address);
        Assert.Equal(1420, state.Mtu);
    }

    // An interface that is up with no address of its own routes nothing. Reporting null rather
    // than guessing is what lets the caller treat it as a mismatch and rebuild the interface.
    [Fact]
    public void Reports_a_missing_address_as_null()
    {
        var state = AwgRuntime.ParseLinkState(
            "4: awg0: <POINTOPOINT,NOARP,UP,LOWER_UP> mtu 1420 qdisc noqueue state UNKNOWN\n    link/none\n");

        Assert.Null(state.Address);
        Assert.Equal(1420, state.Mtu);
    }

    [Fact]
    public void Reports_nothing_for_output_it_does_not_understand()
    {
        var state = AwgRuntime.ParseLinkState("Device \"awg0\" does not exist.");

        Assert.Null(state.Address);
        Assert.Null(state.Mtu);
    }
}

public class NodeFirewallTests
{
    // The hooks and the agent's own check have to describe the same rules, or `iptables -C` never
    // matches what PostUp installed and every apply appends a duplicate.
    [Fact]
    public void Renders_the_same_rules_into_the_hooks_that_it_checks_at_runtime()
    {
        var config = ServerConfigRenderer.Render(
            new DesiredStateBundle(
                DesiredStateBundle.CurrentSchemaVersion,
                1,
                "node-a",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(10),
                new FleetIdentity(1, "SERVER_PRIVATE", "SERVER_PUBLIC"),
                new NetworkProfile("10.8.0.0/24", 51820),
                null,
                new NodeSettings("awg0", null, null),
                []),
            "ens3");

        foreach (var rule in NodeFirewall.Rules("%i", "ens3"))
        {
            Assert.Contains(rule.ToCommand("-A"), config);
            Assert.Contains(rule.ToCommand("-D"), config);
        }
    }

    [Fact]
    public void Checks_a_rule_with_the_same_arguments_it_would_append()
    {
        var masquerade = NodeFirewall.Rules("awg0", "ens3").Single(rule => rule.Table == "nat");

        Assert.Equal(["-t", "nat", "-C", "POSTROUTING", "-o", "ens3", "-j", "MASQUERADE"], masquerade.ToArguments("-C"));
        Assert.Equal(["-t", "nat", "-A", "POSTROUTING", "-o", "ens3", "-j", "MASQUERADE"], masquerade.ToArguments("-A"));
    }

    [Fact]
    public void Accepts_forwarding_in_both_directions()
    {
        var filter = NodeFirewall.Rules("awg0", "ens3").Where(rule => rule.Table is null).ToArray();

        Assert.Equal(["-A", "FORWARD", "-i", "awg0", "-j", "ACCEPT"], filter[0].ToArguments("-A"));
        Assert.Equal(["-A", "FORWARD", "-o", "awg0", "-j", "ACCEPT"], filter[1].ToArguments("-A"));
    }

    // The config on disk is the only record of which interface the running MASQUERADE names, so
    // reading it back has to agree with what the renderer wrote.
    [Fact]
    public void Reads_back_the_egress_interface_a_rendered_config_masquerades_through()
    {
        var config = ServerConfigRenderer.Render(Bundle(), "enp1s0");

        Assert.Equal("enp1s0", NodeFirewall.InstalledEgress(config));
    }

    [Fact]
    public void Finds_no_egress_in_a_config_without_hooks()
        => Assert.Null(NodeFirewall.InstalledEgress("[Interface]\nPrivateKey = x\n"));

    // A changed egress leaves exactly one rule behind: the MASQUERADE out of the old interface.
    // The FORWARD rules name the tunnel, not the egress, and must survive - withdrawing them would
    // cut the traffic the new rule was installed to carry.
    [Fact]
    public void Treats_only_the_old_masquerade_as_stale_when_the_egress_moves()
    {
        var stale = NodeFirewall.Stale("awg0", previousEgress: "eth0", currentEgress: "ens3");

        var rule = Assert.Single(stale);
        Assert.Equal(["-t", "nat", "-D", "POSTROUTING", "-o", "eth0", "-j", "MASQUERADE"], rule.ToArguments("-D"));
    }

    [Fact]
    public void Treats_nothing_as_stale_when_the_egress_is_unchanged()
        => Assert.Empty(NodeFirewall.Stale("awg0", "ens3", "ens3"));

    private static DesiredStateBundle Bundle()
        => new(
            DesiredStateBundle.CurrentSchemaVersion,
            1,
            "node-a",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(10),
            new FleetIdentity(1, "SERVER_PRIVATE", "SERVER_PUBLIC"),
            new NetworkProfile("10.8.0.0/24", 51820),
            null,
            new NodeSettings("awg0", null, null),
            []);
}

public class HandshakeProbeParsingTests
{
    // A fresh probe interface reads 0 until its first handshake. Reading 0 as a handshake at the
    // epoch would report every blocked node as reachable.
    [Fact]
    public void Reads_zero_as_no_handshake_yet()
        => Assert.Null(HandshakeProbe.LatestHandshake("SERVER_PUBLIC=\t0\n"));

    [Fact]
    public void Reads_a_completed_handshake()
        => Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_790_000_000),
            HandshakeProbe.LatestHandshake("SERVER_PUBLIC=\t1790000000\n"));

    [Fact]
    public void Reads_nothing_from_output_it_does_not_understand()
        => Assert.Null(HandshakeProbe.LatestHandshake("Unable to access interface: No such device"));
}
