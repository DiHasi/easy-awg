using System.Diagnostics;
using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Checks one node the only way that means anything for a client: by connecting to it as a
/// client does - the fleet's wire format, a real client key - and getting traffic through.
///
/// A TCP connect or a ping would pass straight through the blocks that matter. DPI that
/// recognises the protocol, or a filter on the UDP port, leaves the address answering everything
/// else - the node looks reachable and no client connects. So the probe brings up a throwaway
/// interface aimed at the node's address and lets the keepalive start a handshake. A handshake is
/// still not the answer: the block seen in practice lets the first handshake through and drops
/// everything after it. So once it completes, the probe fetches a resource through the tunnel
/// (<see cref="TunnelTrafficCheck"/>), and only an answer counts as reachable.
///
/// It keeps its own failures apart from the node's. If the probe cannot even bring its interface
/// up, that is an <see cref="ProbeOutcomes.Error"/>, which the panel ignores: a probe whose
/// tooling broke must never be able to move a fleet's traffic.
/// </summary>
public sealed class HandshakeProbe(NodeOptions options, TunnelTrafficCheck traffic, ILogger<HandshakeProbe> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<ProbeResult> CheckAsync(
        ProbeTarget target,
        TimeSpan timeout,
        IReadOnlyList<string> checkUrls,
        TimeSpan trafficTimeout,
        CancellationToken cancellationToken)
    {
        // A crash mid-check can leave the interface behind, and awg-quick refuses to bring up an
        // interface that already exists.
        await RemoveInterfaceAsync(cancellationToken);

        var directory = Path.GetDirectoryName(options.InterfaceConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The config carries this probe's private key.
        await File.WriteAllTextAsync(options.InterfaceConfigPath, target.Config, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(options.InterfaceConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var up = await ProcessRunner.TryRunAsync("awg-quick", ["up", options.InterfaceConfigPath], input: null, cancellationToken);
            if (up is null)
            {
                return Result(target, ProbeOutcomes.Error, null, "awg-quick is not available on the probe.");
            }

            if (up.ExitCode != 0)
            {
                return Result(target, ProbeOutcomes.Error, null, $"Could not bring up the probe interface: {Tail(up.Error)}");
            }

            // A fresh interface reads 0 until its first handshake, so any other value is this one.
            while (clock.Elapsed < timeout)
            {
                var show = await ProcessRunner.TryRunAsync("awg", ["show", options.InterfaceName, "latest-handshakes"], input: null, cancellationToken);
                if (show is null || show.ExitCode != 0)
                {
                    return Result(target, ProbeOutcomes.Error, null, $"Could not read the probe interface: {Tail(show?.Error)}");
                }

                if (LatestHandshake(show.Output) is not null)
                {
                    return await CheckTrafficAsync(target, (int)clock.ElapsedMilliseconds, checkUrls, trafficTimeout, cancellationToken);
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            return Result(
                target,
                ProbeOutcomes.Unreachable,
                null,
                $"No handshake from {target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)} within "
                + $"{((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s.",
                handshake: false);
        }
        finally
        {
            await RemoveInterfaceAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Parses `awg show &lt;iface&gt; latest-handshakes`: one line per peer, the public key and a
    /// unix timestamp, 0 for never.
    /// </summary>
    internal static DateTimeOffset? LatestHandshake(string output)
    {
        DateTimeOffset? latest = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2
                && long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
                latest = latest is null || at > latest ? at : latest;
            }
        }

        return latest;
    }

    private async Task RemoveInterfaceAsync(CancellationToken cancellationToken)
    {
        var exists = await ProcessRunner.TryRunAsync("ip", ["link", "show", "dev", options.InterfaceName], input: null, cancellationToken);
        if (exists is null || exists.ExitCode != 0)
        {
            return;
        }

        if (File.Exists(options.InterfaceConfigPath))
        {
            var down = await ProcessRunner.TryRunAsync("awg-quick", ["down", options.InterfaceConfigPath], input: null, cancellationToken);
            if (down is { ExitCode: 0 })
            {
                return;
            }
        }

        logger.LogDebug("awg-quick down did not remove {InterfaceName}; deleting the link.", options.InterfaceName);
        await ProcessRunner.TryRunAsync("ip", ["link", "delete", "dev", options.InterfaceName], input: null, cancellationToken);
    }

    private async Task<ProbeResult> CheckTrafficAsync(
        ProbeTarget target,
        int handshakeMs,
        IReadOnlyList<string> checkUrls,
        TimeSpan trafficTimeout,
        CancellationToken cancellationToken)
    {
        // A panel that asks for nothing gets the handshake alone, said plainly rather than passed
        // off as the full check.
        if (checkUrls.Count == 0)
        {
            return Result(target, ProbeOutcomes.Reachable, handshakeMs, "Handshake only: no check URL configured.", handshake: true);
        }

        var result = await traffic.CheckAsync(options.InterfaceName, checkUrls, trafficTimeout, cancellationToken);
        if (result.ProbeFault)
        {
            return Result(target, ProbeOutcomes.Error, null, result.Detail, handshake: true);
        }

        return result.Passed
            ? Result(target, ProbeOutcomes.Reachable, result.LatencyMs, result.Detail, handshake: true)
            : Result(
                target,
                ProbeOutcomes.Unreachable,
                null,
                $"Handshake completed, but no traffic came back through the tunnel ({result.Detail}).",
                handshake: true);
    }

    private static ProbeResult Result(ProbeTarget target, string outcome, int? latencyMs, string? detail, bool? handshake = null)
        => new(target.NodeId, target.Address, outcome, DateTimeOffset.UtcNow, latencyMs, detail, handshake);

    private static string Tail(string? error)
    {
        var text = (error ?? string.Empty).Trim();
        return text.Length <= 300 ? text : text[^300..];
    }
}
