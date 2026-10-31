namespace AwgEasy.Control;

/// <summary>What one node is carrying: peers handshaking through it now, and peers pointed at it
/// and quiet.</summary>
public readonly record struct NodePeerLoad(int Online, int Idle)
{
    public int Total => Online + Idle;
}

/// <param name="Online">
/// False is not "gone": it is a peer whose last known node is this one and which has nothing to
/// say right now, which is the state a shutdown breaks quietly.
/// </param>
public readonly record struct PeerOnNode(string PublicKey, DateTimeOffset? Handshake, bool Online);

/// <param name="Peers">Every key the fleet holds a reading for, counters folded across nodes.</param>
/// <param name="Nodes">The counts, over the peers that are people. Every node asked for is here.</param>
/// <param name="PeersByNode">The same peers, so a count and the list behind it come from one reading.</param>
public sealed record PeerPresenceMap(
    IReadOnlyDictionary<string, PeerTotals> Peers,
    IReadOnlyDictionary<string, NodePeerLoad> Nodes,
    IReadOnlyDictionary<string, IReadOnlyList<PeerOnNode>> PeersByNode)
{
    public NodePeerLoad For(string nodeId) => Nodes.GetValueOrDefault(nodeId);

    public IReadOnlyList<PeerOnNode> On(string nodeId) => PeersByNode.GetValueOrDefault(nodeId, []);
}

/// <summary>
/// Which node each peer is on, and therefore who is still on the node an operator is about to
/// power off. The panel could not answer that, and the question is sharper than it looks: a
/// running tunnel keeps the address it resolved when it came up, so repointing the record decides
/// where the next connection goes and moves nobody who is already connected. A node an operator
/// believes they have drained can keep carrying people for as long as their clients keep running.
///
/// The evidence is a handshake and nothing stronger. A peer that sends nothing and has no
/// persistent keepalive produces none at all, so silence is indistinguishable from departure - the
/// idle number is a ceiling on who would notice, not a count of them.
///
/// Pure, like <see cref="NodeHealthEvaluator"/>, so the rule is testable without a panel. And one
/// rule with several readers: the number on a node's card, the list behind it and the node named
/// in a peer's row disagreeing about the same peer on the same page would be worse than none of
/// them.
/// </summary>
public static class PeerPresence
{
    /// <summary>
    /// A peer with no handshake inside this is not carrying traffic: AmneziaWG rekeys well inside
    /// three minutes, so a longer silence means the tunnel is idle or gone.
    ///
    /// Deliberately not a setting. It is a claim about what AmneziaWG does, not about a
    /// deployment, and there is nothing in the fleet to derive it from - the rekey and keepalive
    /// values are per-peer obfuscation strings an operator may well set to ranges. A window
    /// somebody could shorten would have the drain count call a node empty while people are on it.
    /// </summary>
    public static readonly TimeSpan CurrentWithin = TimeSpan.FromMinutes(3);

    public static bool IsCurrent(DateTimeOffset? handshake, DateTimeOffset now)
        => handshake is { } seen && now - seen <= CurrentWithin;

    /// <summary>
    /// Folds the fleet's readings per peer: a client may be connected through any node and may
    /// move between them, so the newest handshake wins and carries its node, and byte counters are
    /// summed.
    ///
    /// Clock-free on purpose - *where* a peer is does not depend on the time, only whether it is
    /// current does.
    /// </summary>
    public static IReadOnlyDictionary<string, PeerTotals> Totals(IEnumerable<PeerStatRow> rows)
    {
        var totals = new Dictionary<string, PeerTotals>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!totals.TryGetValue(row.PublicKey, out var held))
            {
                totals[row.PublicKey] = new PeerTotals(row.Handshake, row.Rx, row.Tx, row.NodeId, row.ResetAt);
                continue;
            }

            var keep = Wins(held.Handshake, held.NodeId, row.Handshake, row.NodeId);
            totals[row.PublicKey] = new PeerTotals(
                keep ? held.Handshake : row.Handshake,
                held.Rx + row.Rx,
                held.Tx + row.Tx,
                keep ? held.NodeId : row.NodeId,
                AtLeastAsNew(held.ResetAt, row.ResetAt) ? held.ResetAt : row.ResetAt);
        }

        return totals;
    }

    /// <summary>
    /// The same fold, read as "who is where": the counted peers placed on the node that saw their
    /// newest handshake, split by whether that handshake is current.
    ///
    /// A peer lands on exactly one node even when two of them report a current handshake for it -
    /// two devices sharing a config, or a peer caught mid-move. The counts then partition the
    /// peers that are online, so the arithmetic an operator does while draining holds: the numbers
    /// across the nodes add up to the people who are connected. What that costs is the second
    /// session, which no number here can show, so the dialog says so in words.
    /// </summary>
    /// <param name="counted">
    /// The public keys of enabled peers that belong to people. Probes are left out deliberately: a
    /// probe is a peer on every node by design and handshakes with all of them, so counting one
    /// would show every node permanently occupied - the one way to get this badly wrong. Disabled
    /// peers are left out so a node cannot claim to carry somebody the peer list draws as off.
    /// </param>
    /// <param name="nodes">
    /// The nodes that still exist, revoked ones included: revoking stops bundles, not traffic, so a
    /// revoked node carrying people is the sharpest case there is. Rows left by a node that was
    /// deleted are ignored rather than stranding its peers on a server that is gone.
    /// </param>
    public static PeerPresenceMap Assess(
        IEnumerable<PeerStatRow> rows,
        IReadOnlySet<string> counted,
        IReadOnlySet<string> nodes,
        DateTimeOffset now)
    {
        var totals = Totals(rows);
        var load = nodes.ToDictionary(id => id, _ => default(NodePeerLoad), StringComparer.Ordinal);
        var peers = nodes.ToDictionary(id => id, _ => new List<PeerOnNode>(), StringComparer.Ordinal);

        foreach (var (key, peer) in totals)
        {
            // No handshake anywhere is a config that was issued and never used: nobody this node
            // would disconnect, so it is placed nowhere rather than placed arbitrarily.
            if (peer.Handshake is null
                || peer.NodeId is not { } nodeId
                || !counted.Contains(key)
                || !load.TryGetValue(nodeId, out var count))
            {
                continue;
            }

            var online = IsCurrent(peer.Handshake, now);
            load[nodeId] = online ? count with { Online = count.Online + 1 } : count with { Idle = count.Idle + 1 };
            peers[nodeId].Add(new PeerOnNode(key, peer.Handshake, online));
        }

        return new PeerPresenceMap(
            totals,
            load,
            peers.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<PeerOnNode>)[.. entry.Value.OrderByDescending(peer => peer.Handshake)],
                StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether the reading already held beats the one being offered.
    ///
    /// The tie-break is load-bearing rather than tidy. Two nodes can report the very same
    /// handshake - and with none at all, every node reports the same nothing - and without a rule
    /// the winner is whichever row the scan returned first.
    /// <see cref="NodeRepository.ReplacePeerStats"/> re-inserts a node's rows at the end of the
    /// table on every report, so that order changes every twenty seconds: the node named beside a
    /// peer would flip between two refreshes of a page that polls every ten.
    /// </summary>
    private static bool Wins(DateTimeOffset? held, string? heldNode, DateTimeOffset? candidate, string? candidateNode)
        => held == candidate
            ? string.CompareOrdinal(heldNode, candidateNode) <= 0
            : AtLeastAsNew(held, candidate);

    /// <summary>
    /// Null is no reading at all, which is older than any reading - never newer. Comparing two
    /// nullable timestamps with <c>&gt;=</c> answers false in both directions when either is null,
    /// so a node that reports a peer with no handshake would erase the handshake another node
    /// reported, depending only on the order the scan happened to return the rows in. Every node
    /// carries every peer, so in a fleet most nodes report most peers as never seen, and
    /// <see cref="NodeRepository.ReplacePeerStats"/> re-inserts a node's rows at the end of the
    /// table on every report - which is what made the whole list flip to "never" and back between
    /// two refreshes.
    /// </summary>
    internal static bool AtLeastAsNew(DateTimeOffset? held, DateTimeOffset? candidate)
        => candidate is null || (held is { } seen && seen >= candidate.Value);
}

/// <summary>
/// Reads the fleet's peer readings once and asks <see cref="PeerPresence"/> where everybody is.
///
/// Thin on purpose: the rule stays pure and testable, and the handlers that need a count get one
/// dependency instead of two repositories and the plumbing between them - which is also what
/// keeps every reader counting the same nodes.
/// </summary>
public sealed class PeerPresenceService(NodeRepository nodes, ClientRepository clients)
{
    /// <param name="fleet">
    /// Every node the panel holds, never a subset. Which node a peer is on is decided by comparing
    /// what all of them reported, so asking about one node alone would credit it with a peer
    /// another node saw more recently.
    /// </param>
    public PeerPresenceMap Assess(IEnumerable<NodeRecord> fleet, DateTimeOffset now)
        => PeerPresence.Assess(
            nodes.ReadPeerStats(),
            clients.ServedPublicKeys(),
            fleet.Select(node => node.Id).ToHashSet(StringComparer.Ordinal),
            now);
}
