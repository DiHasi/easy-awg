using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>One iptables rule, in the form both the config hooks and the reconciler need.</summary>
/// <param name="Arguments">The chain first, then the match and the target.</param>
/// <param name="InsertAt">
/// Where the rule belongs in its chain, for the rules whose position is load-bearing: an add
/// renders as `-I &lt;chain&gt; &lt;position&gt;` instead of `-A`. Null appends, which is what every rule
/// here did before one of them had to sit above another.
/// </param>
public sealed record IptablesRule(string? Table, string[] Arguments, int? InsertAt = null)
{
    private const string Add = "-A";

    /// <summary>The rule as a shell command, for the PostUp/PostDown hooks awg-quick runs.</summary>
    public string ToCommand(string op) => string.Join(' ', ["iptables", .. ToArguments(op)]);

    /// <summary>The rule as an argument list, for running iptables directly.</summary>
    public string[] ToArguments(string op)
    {
        // Only an add can be positioned: a delete or a check matches the rule wherever it sits,
        // and handing either one a position is an iptables syntax error.
        string[] applied = op == Add && InsertAt is { } position
            ? ["-I", Arguments[0], position.ToString(CultureInfo.InvariantCulture), .. Arguments[1..]]
            : [op, .. Arguments];

        return Table is null ? applied : ["-t", Table, .. applied];
    }
}

/// <summary>
/// The packet-forwarding rules a node needs, in one place.
///
/// awg-quick installs them through PostUp, which runs only when the interface comes up. Every
/// later revision goes through `awg syncconf`, and syncconf runs no hooks - so a rule flushed by
/// a Docker daemon restart, or an interface the agent found already up, leaves a tunnel that
/// handshakes, reports healthy and carries nothing. The agent therefore re-checks them on every
/// apply, and both paths build them from this one list so the two cannot drift apart.
/// </summary>
public static class NodeFirewall
{
    /// <summary>
    /// The agent's own chain in the nat table, holding one SNAT rule per peer that leaves through
    /// an address of its own, plus the node's default at the end.
    ///
    /// A chain rather than rules in POSTROUTING directly, because these are the only rules here
    /// that go stale on their own: a pin disappears when its peer is deleted or re-labelled, and
    /// the check-then-append loop that keeps the other rules alive has no way to notice a rule
    /// that ought to be gone. A chain can be read whole, compared and rebuilt - which is also the
    /// only way to be sure of the order inside it, where the per-peer rules have to be seen
    /// before the node default that covers the whole subnet.
    /// </summary>
    public const string EgressChain = "AWG_EGRESS";

    /// <summary>
    /// Creates the chain the jump rule names. `|| true` because `-N` on a chain that already
    /// exists is an error and nothing else, and the hooks run inside awg-quick's `set -e`: a
    /// second bring-up must not abort because the chain outlived the first.
    /// </summary>
    public static readonly string ChainSetup = $"iptables -t nat -N {EgressChain} 2>/dev/null || true";

    /// <summary>Emptied and removed only after the jump to it is gone, or iptables refuses.</summary>
    public static readonly string ChainTeardown =
        $"iptables -t nat -F {EgressChain} 2>/dev/null || true; iptables -t nat -X {EgressChain} 2>/dev/null || true";

    /// <param name="interfaceToken">The interface name, or awg-quick's `%i` when writing hooks.</param>
    public static IptablesRule[] Rules(string interfaceToken, string egressInterface) =>
    [
        // Both directions, not just the one out of the tunnel. The agent runs in Docker, and
        // Docker sets the host FORWARD policy to DROP, so a rule for `-i %i` alone lets a client
        // reach the internet while every reply is dropped on the way back: the tunnel handshakes,
        // the node decrypts and masquerades, and the client still sees nothing. On a host without
        // Docker the policy is ACCEPT and the missing rule costs nothing, which is what kept this
        // hidden.
        new IptablesRule(null, ["FORWARD", "-i", interfaceToken, "-j", "ACCEPT"]),
        new IptablesRule(null, ["FORWARD", "-o", interfaceToken, "-j", "ACCEPT"]),
        // Inserted rather than appended, and the one rule here whose position matters. SNAT is a
        // terminating target, so it is only reached for a pinned peer if the jump is seen before
        // the blanket MASQUERADE below - and on an upgrade that rule is already installed, so
        // appending the jump would quietly leave every pin unapplied. Position 1 also puts it
        // above whatever else writes to POSTROUTING, Docker included, which costs nothing: a
        // packet that matches no rule inside the chain returns and carries on down POSTROUTING.
        new IptablesRule("nat", ["POSTROUTING", "-o", egressInterface, "-j", EgressChain], InsertAt: 1),
        // Still the fall-through, and still installed by the hooks, so a node that comes up
        // without its agent NATs exactly as it always did: an unapplied pin costs a peer the
        // address it was meant to leave with, never its traffic.
        new IptablesRule("nat", ["POSTROUTING", "-o", egressInterface, "-j", "MASQUERADE"])
    ];

    /// <summary>
    /// What <see cref="EgressChain"/> should hold for this bundle: the peers that leave through
    /// an address of their own, then the node default for everyone else.
    ///
    /// A pin naming the node's own default is left out - it would be a rule that changes nothing,
    /// and a fleet where everybody is pinned to one address would otherwise write one per peer.
    /// </summary>
    /// <param name="available">
    /// The addresses the node actually holds, or null when that could not be determined. An
    /// address this host does not carry is skipped: SNAT to it leaves that peer with no working
    /// traffic at all, where skipping leaves it on the default. Null is no evidence either way,
    /// and then what the panel published is applied as it stands.
    /// </param>
    public static IptablesRule[] EgressRules(DesiredStateBundle bundle, IReadOnlySet<string>? available = null)
    {
        var fallback = bundle.Node.DefaultEgressAddress;
        var rules = new List<IptablesRule>();

        foreach (var peer in bundle.Peers)
        {
            if (peer.EgressAddress is not { } address
                || string.Equals(address, fallback, StringComparison.Ordinal)
                || available?.Contains(address) == false)
            {
                continue;
            }

            rules.Add(Snat($"{peer.Address}/32", address));
        }

        if (fallback is not null && available?.Contains(fallback) != false)
        {
            rules.Add(Snat(bundle.Network.Subnet, fallback));
        }

        return [.. rules];
    }

    /// <summary>Every address this bundle asks the node to leave through, pins and default alike.</summary>
    public static string[] ConfiguredEgressAddresses(DesiredStateBundle bundle)
        => [.. bundle.Peers
            .Select(peer => peer.EgressAddress)
            .Append(bundle.Node.DefaultEgressAddress)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The rules of <see cref="EgressChain"/> as an `iptables -S` listing describes them.
    ///
    /// Read as what each rule means - this source leaves as that address - rather than as the
    /// text iptables prints, because the comparison runs on every apply and a rebuild loop caused
    /// by a difference in spacing would flush the chain every twenty seconds. A line that is not
    /// a SNAT of a source is kept verbatim so it can never match anything desired: nothing but
    /// the agent writes to this chain, and something that did is reason enough to rebuild it.
    /// </summary>
    internal static string[] ParseEgressChain(string listing)
    {
        var parsed = new List<string>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens is not ["-A", EgressChain, ..])
            {
                continue;
            }

            parsed.Add(Describe(tokens[2..]) ?? line);
        }

        return [.. parsed];
    }

    /// <summary>The same reading of the rules the agent means to install, for that comparison.</summary>
    internal static string[] DescribeEgressRules(IptablesRule[] rules)
        => [.. rules.Select(rule => Describe(rule.Arguments[1..]) ?? string.Join(' ', rule.Arguments))];

    private static IptablesRule Snat(string source, string address)
        => new("nat", [EgressChain, "-s", source, "-j", "SNAT", "--to-source", address]);

    private static string? Describe(string[] arguments)
    {
        string? source = null;
        string? target = null;

        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == "-s")
            {
                // iptables prints a host as /32 and accepts it either way, so both sides are read
                // the same way rather than compared as written.
                source = arguments[i + 1].Contains('/', StringComparison.Ordinal)
                    ? arguments[i + 1]
                    : arguments[i + 1] + "/32";
            }
            else if (arguments[i] == "--to-source")
            {
                target = arguments[i + 1];
            }
        }

        return source is null || target is null ? null : $"{source} -> {target}";
    }

    /// <summary>
    /// The rules as one PostUp (`-A`) or PostDown (`-D`) line, with the egress chain created
    /// before the rule that jumps to it and removed after that rule is gone.
    /// </summary>
    public static string Hook(string op, string interfaceToken, string egressInterface)
    {
        var rules = Rules(interfaceToken, egressInterface).Select(rule => rule.ToCommand(op));
        return op == "-A"
            ? string.Join("; ", [ChainSetup, .. rules])
            : string.Join("; ", [.. rules, ChainTeardown]);
    }

    /// <summary>
    /// The rules a previous configuration installed that the current one does not. Only the nat
    /// rules name the egress interface, so in practice this is the MASQUERADE and the jump to the
    /// egress chain out of an interface the node no longer routes through.
    /// </summary>
    public static IptablesRule[] Stale(string interfaceName, string previousEgress, string currentEgress)
    {
        var current = Rules(interfaceName, currentEgress).Select(rule => rule.ToCommand("-A")).ToHashSet(StringComparer.Ordinal);
        return [.. Rules(interfaceName, previousEgress).Where(rule => !current.Contains(rule.ToCommand("-A")))];
    }

    /// <summary>
    /// Reads which egress interface a rendered config masquerades through, from its PostUp hook.
    ///
    /// The config on disk is the only record of what the running rules were built from: the
    /// resolver caches its answer per process, so after an agent restart - or a changed override
    /// in the bundle - nothing else remembers the interface the old rule named.
    /// </summary>
    public static string? InstalledEgress(string config)
    {
        foreach (var line in config.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("PostUp", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length - 2; i++)
            {
                if (tokens[i] == "POSTROUTING" && tokens[i + 1] == "-o")
                {
                    return tokens[i + 2];
                }
            }
        }

        return null;
    }
}
