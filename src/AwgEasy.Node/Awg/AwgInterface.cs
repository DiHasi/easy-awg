using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Writes the rendered interface config and brings the tunnel in line with it.
///
/// Prefers `awg syncconf` when the interface is already up, so peers change without dropping the
/// interface - existing tunnels survive a peer-list update. But syncconf carries only what lives
/// on the device: keys, listen port, obfuscation and peers. The address, the MTU and the firewall
/// hooks are awg-quick's, and `awg-quick strip` removes them before syncconf ever sees them. An
/// interface that outlives the configuration that created it therefore keeps the old address
/// indefinitely while the agent reports every new revision as applied - a node sitting in the
/// previous deployment's subnet, decrypting client packets and dropping every one of them for
/// failing the AllowedIPs check. So those settings are compared against the running interface
/// first, and a mismatch costs a full restart rather than a silent lie.
/// </summary>
public sealed class AwgInterface(
    NodeOptions options,
    AwgRuntime runtime,
    EgressInterfaceResolver egressResolver,
    EgressAddressResolver addressResolver,
    ILogger<AwgInterface> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ApplyAsync(DesiredStateBundle bundle, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var egress = await egressResolver.ResolveAsync(bundle.Node.EgressInterface, cancellationToken);
            var config = ServerConfigRenderer.Render(bundle, egress);

            var directory = Path.GetDirectoryName(options.InterfaceConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Read before the file is overwritten, for the same reason as the restart decision
            // below: once the new config is on disk, nothing records what the old rules named.
            var previousEgress = File.Exists(options.InterfaceConfigPath)
                ? NodeFirewall.InstalledEgress(await File.ReadAllTextAsync(options.InterfaceConfigPath, cancellationToken))
                : null;

            // Decided, and acted on, before the file is overwritten: taking the interface down
            // runs the PostDown of the config that brought it up, which is the only thing that
            // knows which rules to withdraw.
            var restartReason = await GetRestartReasonAsync(bundle, cancellationToken);
            if (restartReason is not null)
            {
                logger.LogWarning(
                    "Interface {InterfaceName} does not match the bundle ({Reason}). Restarting it; tunnels will drop.",
                    options.InterfaceName,
                    restartReason);
                await BringDownAsync(cancellationToken);
            }

            logger.LogInformation(
                "Writing AmneziaWG config for revision {Revision} ({PeerCount} peers) to {Path}.",
                bundle.Revision,
                bundle.Peers.Length,
                options.InterfaceConfigPath);

            await File.WriteAllTextAsync(options.InterfaceConfigPath, config, cancellationToken);
            SetOwnerOnlyPermissions(options.InterfaceConfigPath);

            if (restartReason is null && await runtime.InterfaceExistsAsync(cancellationToken))
            {
                await SyncExistingInterfaceAsync(cancellationToken);
            }
            else
            {
                logger.LogInformation("Bringing up interface {InterfaceName}.", options.InterfaceName);
                await RunAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
            }

            await EnsureFirewallAsync(bundle, egress, cancellationToken);

            if (previousEgress is not null && !string.Equals(previousEgress, egress, StringComparison.Ordinal))
            {
                await WithdrawStaleRulesAsync(previousEgress, egress, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Why the running interface cannot be synced in place, or null when it can. Only the
    /// settings syncconf leaves alone are checked: everything else it applies itself.
    /// </summary>
    private async Task<string?> GetRestartReasonAsync(DesiredStateBundle bundle, CancellationToken cancellationToken)
    {
        if (!await runtime.InterfaceExistsAsync(cancellationToken))
        {
            return null;
        }

        var network = Ipv4Network.Parse(bundle.Network.Subnet);
        var desiredAddress = $"{network.GatewayAddress}/{network.PrefixLength.ToString(CultureInfo.InvariantCulture)}";
        var state = await runtime.GetLinkStateAsync(cancellationToken);

        if (state.Address is null)
        {
            return "it carries no IPv4 address";
        }

        if (!string.Equals(state.Address, desiredAddress, StringComparison.Ordinal))
        {
            return $"address is {state.Address}, the bundle says {desiredAddress}";
        }

        // An unset MTU leaves awg-quick to work one out from the route, so there is nothing to
        // compare against and nothing to correct.
        if (bundle.Node.Mtu is { } desiredMtu && state.Mtu is { } actualMtu && actualMtu != desiredMtu)
        {
            return $"MTU is {actualMtu.ToString(CultureInfo.InvariantCulture)}, the bundle says {desiredMtu.ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    private async Task BringDownAsync(CancellationToken cancellationToken)
    {
        var down = await RunAsync("awg-quick", ["down", options.InterfaceConfigPath], cancellationToken);
        if (down is not null && down.ExitCode == 0)
        {
            return;
        }

        if (!await runtime.InterfaceExistsAsync(cancellationToken))
        {
            return;
        }

        // An interface left behind by an earlier deployment has no config file here for
        // awg-quick to read, and until the link is gone `awg-quick up` refuses to run at all.
        // Deleting it directly is the only way through. What that skips is the old PostDown, so
        // its rules stay behind: two additive ACCEPTs and a MASQUERADE out of what is still the
        // right interface. None of them break the tunnel this is clearing the way for.
        logger.LogWarning("awg-quick down did not remove {InterfaceName}. Deleting the link directly.", options.InterfaceName);
        await RunAsync("ip", ["link", "delete", "dev", options.InterfaceName], cancellationToken);
    }

    private async Task SyncExistingInterfaceAsync(CancellationToken cancellationToken)
    {
        var strip = await RunAsync("awg-quick", ["strip", options.InterfaceConfigPath], cancellationToken);
        if (strip is null || strip.ExitCode != 0)
        {
            logger.LogWarning("awg-quick strip failed. Falling back to a full restart.");
            await RestartAsync(cancellationToken);
            return;
        }

        var syncConfigPath = options.InterfaceConfigPath + ".sync";
        await File.WriteAllTextAsync(syncConfigPath, strip.Output, cancellationToken);
        SetOwnerOnlyPermissions(syncConfigPath);

        var sync = await RunAsync("awg", ["syncconf", options.InterfaceName, syncConfigPath], cancellationToken);
        if (sync is not null && sync.ExitCode == 0)
        {
            logger.LogInformation("Applied peer list to running interface {InterfaceName} without dropping tunnels.", options.InterfaceName);
            return;
        }

        logger.LogWarning("awg syncconf failed. Falling back to a full restart.");
        await RestartAsync(cancellationToken);
    }

    /// <summary>
    /// `awg-quick up` refuses to run while the interface exists, so a fallback that only brought
    /// it up left the node on the previous configuration and logged a second failure.
    /// </summary>
    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await BringDownAsync(cancellationToken);
        await RunAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
    }

    /// <summary>
    /// Re-checks the forwarding rules on every apply. They are installed by PostUp, which runs
    /// only at bring-up, and nothing reinstalls them afterwards: a Docker daemon restart that
    /// rebuilds the FORWARD chain, or an interface that was already up when the agent arrived,
    /// otherwise leaves a tunnel that is up, in sync, and carries nothing.
    /// </summary>
    private async Task EnsureFirewallAsync(DesiredStateBundle bundle, string egressInterface, CancellationToken cancellationToken)
    {
        // Before the loop, because one of those rules jumps to this chain and iptables will not
        // install a rule naming a chain that does not exist. `-N` on one that already exists is
        // an error and nothing else, so only a missing binary is worth reacting to.
        var created = await ProcessRunner.TryRunAsync(
            "iptables",
            ["-t", "nat", "-N", NodeFirewall.EgressChain],
            input: null,
            cancellationToken);

        if (created is null)
        {
            logger.LogWarning("iptables is not available. Forwarding rules were not checked.");
            return;
        }

        foreach (var rule in NodeFirewall.Rules(options.InterfaceName, egressInterface))
        {
            var check = await ProcessRunner.TryRunAsync("iptables", rule.ToArguments("-C"), input: null, cancellationToken);
            if (check is null)
            {
                logger.LogWarning("iptables is not available. Forwarding rules were not checked.");
                return;
            }

            if (check.ExitCode == 0)
            {
                continue;
            }

            logger.LogWarning("Forwarding rule was missing; restoring it: {Rule}", rule.ToCommand("-A"));
            await RunAsync("iptables", rule.ToArguments("-A"), cancellationToken);
        }

        await ReconcileEgressAsync(bundle, egressInterface, cancellationToken);
    }

    /// <summary>
    /// Brings the egress chain in line with the bundle: one SNAT rule per peer that leaves
    /// through an address of its own, the node default last.
    ///
    /// Compared before it is rebuilt, and rebuilt whole when it differs. Comparing is what keeps
    /// a chain that is already right from being flushed on every poll - a flush is a window, however
    /// short, in which a new connection falls through to the MASQUERADE - and rebuilding whole is
    /// what removes the rule of a peer that has been deleted or re-labelled, which a
    /// check-then-append pass can never notice.
    ///
    /// Nothing here takes the interface down: a changed pin costs nobody their tunnel, and the
    /// translation of connections already established stands until they close.
    /// </summary>
    private async Task ReconcileEgressAsync(DesiredStateBundle bundle, string egressInterface, CancellationToken cancellationToken)
    {
        var available = await addressResolver.ListAsync(egressInterface, cancellationToken);
        var held = available is null ? null : new HashSet<string>(available.Addresses, StringComparer.Ordinal);

        if (held is not null)
        {
            var missing = NodeFirewall.ConfiguredEgressAddresses(bundle).Where(address => !held.Contains(address)).ToArray();
            if (missing.Length > 0)
            {
                // Skipped rather than installed: a rule that SNATs to an address this host does
                // not carry leaves that peer with no working traffic, where leaving it out leaves
                // it on the default. The panel can name who is affected - it has both the plan it
                // published and the addresses this node reports.
                logger.LogWarning(
                    "The bundle asks this node to leave through {Addresses}, which {EgressInterface} does not carry. Those peers keep the default address.",
                    string.Join(", ", missing),
                    egressInterface);
            }
        }

        var desired = NodeFirewall.EgressRules(bundle, held);

        var listing = await ProcessRunner.TryRunAsync(
            "iptables",
            ["-t", "nat", "-S", NodeFirewall.EgressChain],
            input: null,
            cancellationToken);

        if (listing is null || listing.ExitCode != 0)
        {
            return;
        }

        if (NodeFirewall.ParseEgressChain(listing.Output)
            .SequenceEqual(NodeFirewall.DescribeEgressRules(desired), StringComparer.Ordinal))
        {
            return;
        }

        logger.LogInformation(
            "Egress rules changed; rebuilding {Chain} with {Count} rule(s).",
            NodeFirewall.EgressChain,
            desired.Length);

        await RunAsync("iptables", ["-t", "nat", "-F", NodeFirewall.EgressChain], cancellationToken);
        foreach (var rule in desired)
        {
            await RunAsync("iptables", rule.ToArguments("-A"), cancellationToken);
        }
    }

    /// <summary>
    /// Removes what the previous egress interface left behind. A syncconf apply runs no PostDown,
    /// and a later `awg-quick down` runs the PostDown of the new config, which names the new
    /// interface - so without this the old MASQUERADE outlives every restart. A full restart has
    /// usually withdrawn it already through the old PostDown, which is why each rule is checked
    /// before it is deleted rather than deleted blind.
    /// </summary>
    private async Task WithdrawStaleRulesAsync(string previousEgress, string egress, CancellationToken cancellationToken)
    {
        foreach (var rule in NodeFirewall.Stale(options.InterfaceName, previousEgress, egress))
        {
            // Once, not until -C stops matching: a rule the operator added by hand with the same
            // arguments is indistinguishable from ours, and we only ever installed one.
            var check = await ProcessRunner.TryRunAsync("iptables", rule.ToArguments("-C"), input: null, cancellationToken);
            if (check is null || check.ExitCode != 0)
            {
                continue;
            }

            logger.LogInformation(
                "Egress moved from {Previous} to {Current}; withdrawing the old rule: {Rule}",
                previousEgress,
                egress,
                rule.ToCommand("-D"));
            await RunAsync("iptables", rule.ToArguments("-D"), cancellationToken);
        }
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task<ProcessResult?> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.TryRunAsync(fileName, arguments, input: null, cancellationToken);
        if (result is null)
        {
            logger.LogWarning("{Command} is not available. Config was written but not applied.", fileName);
            return null;
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning("{Command} exited with {ExitCode}: {Error}", fileName, result.ExitCode, result.Error);
        }

        return result;
    }
}
