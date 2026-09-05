namespace AwgEasy.Node;

/// <summary>
/// Resolves the interface that NAT should masquerade through.
///
/// The single-server version hardcoded eth0, which silently breaks NAT on hosts where the
/// primary interface is named differently (ens3, enp1s0, eth1 on some providers) - the tunnel
/// comes up, peers handshake, and no traffic reaches the internet. Detect it from the default
/// route instead, and cache the answer for the process lifetime.
/// </summary>
public sealed class EgressInterfaceResolver(NodeOptions options, ILogger<EgressInterfaceResolver> logger)
{
    private const string Fallback = "eth0";
    private string? _resolved;

    public async Task<string> ResolveAsync(string? bundleOverride, CancellationToken cancellationToken)
    {
        // Explicit configuration always wins: env var first, then whatever the control plane set.
        var configured = options.EgressInterface ?? bundleOverride;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (_resolved is not null)
        {
            return _resolved;
        }

        var detected = await DetectAsync(cancellationToken);
        if (detected is null)
        {
            logger.LogWarning(
                "Could not determine the default-route interface; falling back to {Fallback}. Set AWG_EGRESS_INTERFACE if NAT does not work.",
                Fallback);
            _resolved = Fallback;
        }
        else
        {
            logger.LogInformation("Detected egress interface {EgressInterface} from the default route.", detected);
            _resolved = detected;
        }

        return _resolved;
    }

    private async Task<string?> DetectAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.TryRunAsync("ip", ["-4", "route", "show", "default"], input: null, cancellationToken);
        if (result is null || result.ExitCode != 0)
        {
            return null;
        }

        return ParseDefaultRouteInterface(result.Output);
    }

    /// <summary>Parses "default via 10.0.0.1 dev ens3 proto dhcp metric 100" into "ens3".</summary>
    internal static string? ParseDefaultRouteInterface(string routeOutput)
    {
        foreach (var line in routeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == "dev")
                {
                    return tokens[i + 1];
                }
            }
        }

        return null;
    }
}
