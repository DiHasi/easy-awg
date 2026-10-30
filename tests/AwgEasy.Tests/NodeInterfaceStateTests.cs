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
        var masquerade = NodeFirewall.Rules("awg0", "ens3").Last(rule => rule.Table == "nat");

        Assert.Equal(["-t", "nat", "-C", "POSTROUTING", "-o", "ens3", "-j", "MASQUERADE"], masquerade.ToArguments("-C"));
        Assert.Equal(["-t", "nat", "-A", "POSTROUTING", "-o", "ens3", "-j", "MASQUERADE"], masquerade.ToArguments("-A"));
    }

    /// <summary>
    /// SNAT is a terminating target, so the jump into the egress chain is only reached for a
    /// pinned peer if it is seen before the blanket MASQUERADE. On an upgrade that rule is already
    /// installed, so an appended jump would sit below it and every pin would silently do nothing -
    /// which is why this one rule is inserted at a position, and why a check still has to match
    /// the rule wherever it ended up.
    /// </summary>
    [Fact]
    public void Inserts_the_jump_into_the_egress_chain_above_the_masquerade()
    {
        var nat = NodeFirewall.Rules("awg0", "ens3").Where(rule => rule.Table == "nat").ToArray();

        Assert.Equal(["-t", "nat", "-I", "POSTROUTING", "1", "-o", "ens3", "-j", "AWG_EGRESS"], nat[0].ToArguments("-A"));
        Assert.Equal(["-t", "nat", "-C", "POSTROUTING", "-o", "ens3", "-j", "AWG_EGRESS"], nat[0].ToArguments("-C"));
        Assert.Equal(["-t", "nat", "-D", "POSTROUTING", "-o", "ens3", "-j", "AWG_EGRESS"], nat[0].ToArguments("-D"));
        Assert.Contains("MASQUERADE", nat[1].Arguments);
    }

    /// <summary>
    /// The chain has to exist before the rule that jumps to it, and cannot be removed while that
    /// rule is still there. A hook is one `set -e` shell line, so creating a chain that survived
    /// the previous bring-up must not abort the rest of it.
    /// </summary>
    [Fact]
    public void Creates_the_egress_chain_before_the_jump_and_removes_it_after()
    {
        var up = NodeFirewall.Hook("-A", "%i", "ens3");
        var down = NodeFirewall.Hook("-D", "%i", "ens3");

        Assert.StartsWith("iptables -t nat -N AWG_EGRESS 2>/dev/null || true", up, StringComparison.Ordinal);
        Assert.EndsWith("iptables -t nat -X AWG_EGRESS 2>/dev/null || true", down, StringComparison.Ordinal);
        Assert.True(
            down.IndexOf("-D POSTROUTING -o ens3 -j AWG_EGRESS", StringComparison.Ordinal)
            < down.IndexOf("-X AWG_EGRESS", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_one_snat_rule_per_pinned_peer_and_the_node_default_last()
    {
        var rules = NodeFirewall.EgressRules(Bundle(
            defaultEgress: "203.0.113.9",
            peers:
            [
                new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "203.0.113.5"),
                new BundlePeer("PEER_TWO", "PSK_TWO", "10.8.0.3")
            ]));

        Assert.Equal(
            [
                "iptables -t nat -A AWG_EGRESS -s 10.8.0.2/32 -j SNAT --to-source 203.0.113.5",
                "iptables -t nat -A AWG_EGRESS -s 10.8.0.0/24 -j SNAT --to-source 203.0.113.9"
            ],
            rules.Select(rule => rule.ToCommand("-A")));
    }

    /// <summary>
    /// Nothing configured has to leave the installed rule set exactly as it was before any of this
    /// existed: an empty chain, and the MASQUERADE deciding every peer's source as the kernel sees
    /// fit.
    /// </summary>
    [Fact]
    public void Writes_no_rules_at_all_for_a_fleet_that_pins_nobody()
        => Assert.Empty(NodeFirewall.EgressRules(Bundle()));

    [Fact]
    public void Leaves_out_a_pin_that_names_the_node_default()
    {
        var rules = NodeFirewall.EgressRules(Bundle(
            defaultEgress: "203.0.113.9",
            peers: [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "203.0.113.9")]));

        // One rule, the default's: a per-peer rule to the address everybody already leaves with
        // would change nothing, and a fleet pinned wholesale to one address would write one per
        // peer.
        var rule = Assert.Single(rules);
        Assert.Equal("iptables -t nat -A AWG_EGRESS -s 10.8.0.0/24 -j SNAT --to-source 203.0.113.9", rule.ToCommand("-A"));
    }

    /// <summary>
    /// SNAT to an address the host does not carry leaves that peer with no working traffic at all,
    /// where leaving the rule out leaves it on the default. The panel holds both halves of the
    /// discrepancy and can say who is affected.
    /// </summary>
    [Fact]
    public void Skips_an_address_the_node_does_not_carry()
    {
        var bundle = Bundle(
            defaultEgress: "203.0.113.9",
            peers: [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "198.51.100.7")]);

        var rules = NodeFirewall.EgressRules(bundle, new HashSet<string>(["203.0.113.9"], StringComparer.Ordinal));

        Assert.Equal(["198.51.100.7", "203.0.113.9"], NodeFirewall.ConfiguredEgressAddresses(bundle).Order());
        Assert.Equal(
            ["iptables -t nat -A AWG_EGRESS -s 10.8.0.0/24 -j SNAT --to-source 203.0.113.9"],
            rules.Select(rule => rule.ToCommand("-A")));
    }

    /// <summary>
    /// Null is no evidence rather than evidence of nothing: a node whose addresses could not be
    /// read applies what the panel published instead of unpinning everybody.
    /// </summary>
    [Fact]
    public void Installs_what_the_panel_published_when_the_addresses_could_not_be_read()
    {
        var rules = NodeFirewall.EgressRules(
            Bundle(peers: [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "198.51.100.7")]),
            available: null);

        Assert.Single(rules);
    }

    /// <summary>
    /// The comparison that decides whether to rebuild the chain runs on every apply, so it reads
    /// what each rule means rather than the text iptables prints: a chain that is already right
    /// must not be flushed every twenty seconds over a difference in spacing.
    /// </summary>
    [Fact]
    public void Reads_an_installed_chain_as_the_same_rules_it_would_write()
    {
        var desired = NodeFirewall.EgressRules(Bundle(
            defaultEgress: "203.0.113.9",
            peers: [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "203.0.113.5")]));

        var installed = NodeFirewall.ParseEgressChain(
            "-N AWG_EGRESS\n"
            + "-A AWG_EGRESS -s 10.8.0.2/32 -j SNAT --to-source 203.0.113.5\n"
            + "-A AWG_EGRESS -s 10.8.0.0/24 -j SNAT --to-source 203.0.113.9\n");

        Assert.Equal(NodeFirewall.DescribeEgressRules(desired), installed);
    }

    [Theory]
    // A peer that is gone, which a check-then-append pass could never notice.
    [InlineData("-A AWG_EGRESS -s 10.8.0.2/32 -j SNAT --to-source 203.0.113.5\n-A AWG_EGRESS -s 10.8.0.9/32 -j SNAT --to-source 203.0.113.5\n")]
    // The right rules in the wrong order: the default would terminate first and no pin would ever
    // be reached.
    [InlineData("-A AWG_EGRESS -s 10.8.0.0/24 -j SNAT --to-source 203.0.113.9\n-A AWG_EGRESS -s 10.8.0.2/32 -j SNAT --to-source 203.0.113.5\n")]
    // Something nothing here wrote.
    [InlineData("-A AWG_EGRESS -s 10.8.0.2/32 -j MASQUERADE\n")]
    public void Rebuilds_the_chain_when_what_is_installed_differs(string listing)
    {
        var desired = NodeFirewall.EgressRules(Bundle(
            defaultEgress: "203.0.113.9",
            peers: [new BundlePeer("PEER_ONE", "PSK_ONE", "10.8.0.2", "203.0.113.5")]));

        Assert.NotEqual(NodeFirewall.DescribeEgressRules(desired), NodeFirewall.ParseEgressChain(listing));
    }

    // A changed egress leaves behind both rules that name it: the MASQUERADE and the jump into
    // the egress chain. The FORWARD rules name the tunnel, not the egress, and must survive -
    // withdrawing them would cut the traffic the new rules were installed to carry.
    [Fact]
    public void Treats_both_nat_rules_as_stale_when_the_egress_moves()
    {
        var stale = NodeFirewall.Stale("awg0", previousEgress: "eth0", currentEgress: "ens3");

        Assert.Equal(
            [
                ["-t", "nat", "-D", "POSTROUTING", "-o", "eth0", "-j", "AWG_EGRESS"],
                ["-t", "nat", "-D", "POSTROUTING", "-o", "eth0", "-j", "MASQUERADE"]
            ],
            stale.Select(rule => rule.ToArguments("-D")));
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

    [Fact]
    public void Treats_nothing_as_stale_when_the_egress_is_unchanged()
        => Assert.Empty(NodeFirewall.Stale("awg0", "ens3", "ens3"));

    private static DesiredStateBundle Bundle(string? defaultEgress = null, BundlePeer[]? peers = null)
        => new(
            DesiredStateBundle.CurrentSchemaVersion,
            1,
            "node-a",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(10),
            new FleetIdentity(1, "SERVER_PRIVATE", "SERVER_PUBLIC"),
            new NetworkProfile("10.8.0.0/24", 51820),
            null,
            new NodeSettings("awg0", null, null, defaultEgress),
            peers ?? []);
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
