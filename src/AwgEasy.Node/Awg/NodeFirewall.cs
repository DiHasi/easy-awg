namespace AwgEasy.Node;

/// <summary>One iptables rule, in the form both the config hooks and the reconciler need.</summary>
public sealed record IptablesRule(string? Table, string[] Arguments)
{
    /// <summary>The rule as a shell command, for the PostUp/PostDown hooks awg-quick runs.</summary>
    public string ToCommand(string op)
        => Table is null
            ? $"iptables {op} {string.Join(' ', Arguments)}"
            : $"iptables -t {Table} {op} {string.Join(' ', Arguments)}";

    /// <summary>The rule as an argument list, for running iptables directly.</summary>
    public string[] ToArguments(string op)
        => Table is null ? [op, .. Arguments] : ["-t", Table, op, .. Arguments];
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
        new IptablesRule("nat", ["POSTROUTING", "-o", egressInterface, "-j", "MASQUERADE"])
    ];

    /// <summary>The rules as one PostUp (`-A`) or PostDown (`-D`) line.</summary>
    public static string Hook(string op, string interfaceToken, string egressInterface)
        => string.Join("; ", Rules(interfaceToken, egressInterface).Select(rule => rule.ToCommand(op)));

    /// <summary>
    /// The rules a previous configuration installed that the current one does not. Only the
    /// MASQUERADE rule depends on the egress interface, so in practice this is the one rule that
    /// masquerades out of an interface the node no longer routes through.
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
