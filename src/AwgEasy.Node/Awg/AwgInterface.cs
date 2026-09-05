using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Writes the rendered interface config and brings the tunnel in line with it.
///
/// Uses `awg syncconf` when the interface is already up so peers change without dropping the
/// interface - existing tunnels survive a peer-list update. Only falls back to `awg-quick up`
/// when the interface is down or syncconf fails.
/// </summary>
public sealed class AwgInterface(
    NodeOptions options,
    AwgRuntime runtime,
    EgressInterfaceResolver egressResolver,
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

            logger.LogInformation(
                "Writing AmneziaWG config for revision {Revision} ({PeerCount} peers) to {Path}.",
                bundle.Revision,
                bundle.Peers.Length,
                options.InterfaceConfigPath);

            await File.WriteAllTextAsync(options.InterfaceConfigPath, config, cancellationToken);
            SetOwnerOnlyPermissions(options.InterfaceConfigPath);

            if (await runtime.InterfaceExistsAsync(cancellationToken))
            {
                await SyncExistingInterfaceAsync(cancellationToken);
            }
            else
            {
                logger.LogInformation("Interface {InterfaceName} is down. Bringing it up.", options.InterfaceName);
                await RunAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SyncExistingInterfaceAsync(CancellationToken cancellationToken)
    {
        var strip = await RunAsync("awg-quick", ["strip", options.InterfaceConfigPath], cancellationToken);
        if (strip is null || strip.ExitCode != 0)
        {
            logger.LogWarning("awg-quick strip failed. Falling back to awg-quick up.");
            await RunAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
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

        logger.LogWarning("awg syncconf failed. Falling back to awg-quick up.");
        await RunAsync("awg-quick", ["up", options.InterfaceConfigPath], cancellationToken);
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
