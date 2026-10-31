using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Which node each peer is on, which is what an operator is asking before they power one off.
///
/// The rule is pure so it can be read here rather than inferred from a database: every node
/// carries every peer, so most rows are a node reporting that it has never seen somebody, and the
/// answer has to come from comparing what all of them say. What makes it worth testing at this
/// level is that two of the hazards - a null erasing a real reading, and a tie resolved by the
/// order rows happen to arrive in - are invisible until the page flickers.
/// </summary>
public class PeerPresenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static PeerStatRow Row(string node, string key, DateTimeOffset? handshake, long rx = 0, long tx = 0)
        => new(node, key, handshake, rx, tx, null);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    [Fact]
    public void The_newest_handshake_decides_which_node_a_peer_is_on()
    {
        PeerStatRow[] rows =
        [
            Row("ams", "peer", Now.AddSeconds(-90)),
            Row("fra", "peer", Now.AddSeconds(-10))
        ];

        var carrying = PeerPresence.Assess(rows, Set("peer"), Set("ams", "fra"), Now);

        Assert.Equal(new NodePeerLoad(1, 0), carrying.For("fra"));
        Assert.Equal(default, carrying.For("ams"));
        Assert.Equal("peer", Assert.Single(carrying.On("fra")).PublicKey);
    }

    // The hazard PeerStatsTests guards through the database, asserted where the rule lives: a
    // nullable >= is false in both directions, so a node that has never seen a peer could erase
    // the node that has - and every node reports most peers as never seen.
    [Fact]
    public void A_node_that_has_never_seen_a_peer_does_not_claim_it()
    {
        PeerStatRow[] rows =
        [
            Row("ams", "peer", Now.AddSeconds(-20)),
            Row("fra", "peer", null)
        ];

        var carrying = PeerPresence.Assess(rows, Set("peer"), Set("ams", "fra"), Now);

        Assert.Equal(new NodePeerLoad(1, 0), carrying.For("ams"));
        Assert.Equal(default, carrying.For("fra"));
    }

    /// <summary>
    /// Two nodes can report the very same handshake, and with none at all every node reports the
    /// same nothing. Without a tie-break the winner is whichever row the scan returned first -
    /// and a node's rows are deleted and re-inserted on every status report, so that order changes
    /// every twenty seconds while the page polls every ten.
    /// </summary>
    [Fact]
    public void The_same_handshake_on_two_nodes_resolves_the_same_way_whichever_order_the_rows_arrive_in()
    {
        var shared = Now.AddSeconds(-30);
        PeerStatRow[] rows = [Row("ams", "peer", shared), Row("fra", "peer", shared)];

        var forwards = PeerPresence.Totals(rows)["peer"].NodeId;
        var backwards = PeerPresence.Totals(rows.Reverse().ToArray())["peer"].NodeId;

        Assert.Equal(forwards, backwards);
        Assert.Equal("ams", forwards);
    }

    /// <summary>
    /// The arithmetic an operator does while draining: the numbers across the nodes add up to the
    /// people who are connected. A peer current on two nodes at once - two devices on one config,
    /// or a peer caught mid-move - is therefore placed on one of them and not counted twice.
    /// </summary>
    [Fact]
    public void The_counts_partition_the_peers_that_are_connected()
    {
        PeerStatRow[] rows =
        [
            Row("ams", "one", Now.AddSeconds(-5)),
            Row("fra", "one", Now.AddSeconds(-15)),
            Row("ams", "two", Now.AddSeconds(-40)),
            Row("fra", "three", Now.AddSeconds(-20))
        ];

        var carrying = PeerPresence.Assess(rows, Set("one", "two", "three"), Set("ams", "fra"), Now);

        Assert.Equal(2, carrying.For("ams").Online);
        Assert.Equal(1, carrying.For("fra").Online);
        Assert.Equal(3, carrying.Nodes.Values.Sum(load => load.Online));
    }

    [Theory]
    [InlineData(179, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void A_handshake_counts_as_connected_right_up_to_the_edge_of_the_window(int secondsAgo, bool online)
    {
        var carrying = PeerPresence.Assess(
            [Row("ams", "peer", Now.AddSeconds(-secondsAgo))],
            Set("peer"),
            Set("ams"),
            Now);

        Assert.Equal(online ? new NodePeerLoad(1, 0) : new NodePeerLoad(0, 1), carrying.For("ams"));
    }

    /// <summary>
    /// A probe is a peer on every node by design and handshakes with all of them, so counting one
    /// would show every node permanently occupied - which would make the number useless exactly
    /// when it is being relied on.
    /// </summary>
    [Fact]
    public void A_probe_is_counted_nowhere_though_every_node_has_seen_it()
    {
        PeerStatRow[] rows =
        [
            Row("ams", "probe", Now.AddSeconds(-5)),
            Row("fra", "probe", Now.AddSeconds(-5)),
            Row("ams", "peer", Now.AddSeconds(-5))
        ];

        var carrying = PeerPresence.Assess(rows, Set("peer"), Set("ams", "fra"), Now);

        Assert.Equal(new NodePeerLoad(1, 0), carrying.For("ams"));
        Assert.Equal(default, carrying.For("fra"));

        // Still folded, because the counters belong to somebody even when the presence does not.
        Assert.True(carrying.Peers.ContainsKey("probe"));
    }

    [Fact]
    public void A_peer_no_node_has_ever_seen_is_counted_nowhere()
    {
        var carrying = PeerPresence.Assess(
            [Row("ams", "peer", null), Row("fra", "peer", null)],
            Set("peer"),
            Set("ams", "fra"),
            Now);

        Assert.Equal(default, carrying.For("ams"));
        Assert.Equal(default, carrying.For("fra"));
        Assert.Empty(carrying.On("ams"));
    }

    /// <summary>
    /// Nothing prunes peer rows by node on its own, and a deleted node's rows would go on being
    /// the newest handshake anybody reported - stranding that peer on a server the fleet no longer
    /// has, where no count could show it.
    /// </summary>
    [Fact]
    public void Rows_left_behind_by_a_node_that_is_gone_strand_nobody()
    {
        PeerStatRow[] rows =
        [
            Row("deleted", "peer", Now.AddSeconds(-5)),
            Row("ams", "peer", Now.AddSeconds(-45))
        ];

        var carrying = PeerPresence.Assess(rows, Set("peer"), Set("ams"), Now);

        Assert.Equal(default, carrying.For("ams"));
        Assert.DoesNotContain("deleted", carrying.Nodes.Keys);
    }

    /// <summary>
    /// A node whose agent has gone quiet needs no special case: its readings freeze, so its peers
    /// age out of the window on their own and read as pointed here rather than vanishing. The
    /// number an operator must not be given is a zero that reads as "nobody is there".
    /// </summary>
    [Fact]
    public void A_node_nothing_has_been_heard_from_still_holds_the_peers_pointed_at_it()
    {
        PeerStatRow[] rows =
        [
            Row("ams", "one", Now.AddMinutes(-40)),
            Row("ams", "two", Now.AddMinutes(-41))
        ];

        var carrying = PeerPresence.Assess(rows, Set("one", "two"), Set("ams"), Now);

        Assert.Equal(new NodePeerLoad(0, 2), carrying.For("ams"));
        Assert.Equal(2, carrying.For("ams").Total);
    }

    [Fact]
    public void Every_node_asked_about_is_in_the_answer_even_with_nobody_on_it()
    {
        var carrying = PeerPresence.Assess([], Set(), Set("ams", "fra"), Now);

        Assert.Equal(["ams", "fra"], carrying.Nodes.Keys.Order());
        Assert.Empty(carrying.On("ams"));
    }
}
