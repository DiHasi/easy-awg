using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

public sealed record AwgCommandStatus(string Command, int ExitCode, string Output, string Error);

public sealed record AwgToolStatus(string? Awg, string? AwgQuick, string? AmneziawgGo, string? UserspaceImplementation);

public sealed record AwgRuntimeStatus(
    string InterfaceName,
    bool IsRunning,
    string? Backend,
    AwgToolStatus Tools,
    AwgCommandStatus AwgShow,
    AwgCommandStatus IpAddress);

public sealed class AwgRuntime(NodeOptions options, ILogger<AwgRuntime> logger)
{
    public async Task<bool> InterfaceExistsAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.TryRunAsync("awg", ["show", options.InterfaceName], input: null, cancellationToken);
        if (result is null)
        {
            logger.LogWarning("awg is not available. Interface state could not be checked.");
            return false;
        }

        return result.ExitCode == 0;
    }

    public async Task<AwgRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var tools = new AwgToolStatus(
            await GetToolVersionAsync("awg", ["--version"], cancellationToken),
            await GetToolVersionAsync("awg-quick", ["--version"], cancellationToken),
            await GetToolVersionAsync("amneziawg-go", ["--version"], cancellationToken),
            Environment.GetEnvironmentVariable("WG_QUICK_USERSPACE_IMPLEMENTATION"));

        var show = await RunStatusCommandAsync("awg", ["show", options.InterfaceName], cancellationToken);
        var ip = await RunStatusCommandAsync("ip", ["address", "show", "dev", options.InterfaceName], cancellationToken);
        var link = await RunStatusCommandAsync("ip", ["-d", "link", "show", "dev", options.InterfaceName], cancellationToken);
        var backend = DetectBackend(show.ExitCode == 0, link, tools.UserspaceImplementation);
        return new AwgRuntimeStatus(options.InterfaceName, show.ExitCode == 0, backend, tools, show, ip);
    }

    /// <summary>
    /// Reads traffic counters keyed by peer public key. The node has no idea which client a key
    /// belongs to - that mapping lives in the control plane.
    /// </summary>
    public async Task<PeerStatus[]> GetPeerStatusAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.TryRunAsync("awg", ["show", options.InterfaceName, "dump"], input: null, cancellationToken);
        if (result is null)
        {
            logger.LogWarning("awg is not available. Peer traffic counters could not be read.");
            return [];
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning("awg show {InterfaceName} dump exited with {ExitCode}: {Error}", options.InterfaceName, result.ExitCode, result.Error);
            return [];
        }

        return ParseDump(result.Output);
    }

    /// <summary>
    /// Parses `awg show &lt;iface&gt; dump`. The first line describes the interface itself, peers follow:
    /// publickey, presharedkey, endpoint, allowedips, latest-handshake, rx, tx, keepalive.
    /// </summary>
    internal static PeerStatus[] ParseDump(string dump)
    {
        var peers = new List<PeerStatus>();
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split('\t');
            if (columns.Length < 8)
            {
                continue;
            }

            var latestHandshakeAt = TryParseLong(columns[4], out var handshakeUnixSeconds) && handshakeUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(handshakeUnixSeconds)
                : (DateTimeOffset?)null;

            peers.Add(new PeerStatus(
                columns[0],
                latestHandshakeAt,
                TryParseLong(columns[5], out var receivedBytes) ? receivedBytes : 0,
                TryParseLong(columns[6], out var transmittedBytes) ? transmittedBytes : 0));
        }

        return peers.ToArray();
    }

    private static bool TryParseLong(string value, out long result)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static async Task<string?> GetToolVersionAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.TryRunAsync(fileName, arguments, input: null, cancellationToken);
        if (result is null)
        {
            return null;
        }

        var text = (result.Output + result.Error).Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static async Task<AwgCommandStatus> RunStatusCommandAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var command = fileName + " " + string.Join(' ', arguments);
        try
        {
            var result = await ProcessRunner.RunAsync(fileName, arguments, input: null, cancellationToken);
            return new AwgCommandStatus(command, result.ExitCode, result.Output.Trim(), result.Error.Trim());
        }
        catch (Exception exception) when (ProcessRunner.IsMissingExecutable(exception))
        {
            return new AwgCommandStatus(command, -1, string.Empty, exception.Message);
        }
    }

    private static string? DetectBackend(bool isRunning, AwgCommandStatus link, string? userspaceImplementation)
    {
        if (!isRunning)
        {
            return null;
        }

        var linkText = link.Output + Environment.NewLine + link.Error;
        if (link.ExitCode == 0
            && (linkText.Contains("amneziawg", StringComparison.OrdinalIgnoreCase)
                || linkText.Contains("wireguard", StringComparison.OrdinalIgnoreCase)))
        {
            return "Kernel module";
        }

        return string.IsNullOrWhiteSpace(userspaceImplementation) ? "Unknown" : $"Userspace ({userspaceImplementation})";
    }
}
