using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Discovers the address this node is reachable at, which the control plane needs as the target
/// of the DNS record clients follow.
///
/// It has to come from outside the node. The address on the egress interface is often a private
/// one behind the provider's NAT, and the panel cannot use the source address of the agent's own
/// requests either - a TLS-terminating proxy or CDN in front of it would replace that with its
/// own. So the node asks a plain-text echo service and reports what comes back.
///
/// Every failure here is survivable and none of it touches the data plane: a node that cannot
/// look up its address keeps serving traffic and keeps reporting status, it just reports no
/// address, and the panel refuses to point DNS at a node whose address it does not know.
/// </summary>
public sealed class PublicIpResolver(HttpClient http, NodeOptions options, ILogger<PublicIpResolver> logger)
{
    /// <summary>
    /// Retried far sooner than a successful lookup is refreshed: until an address is known this
    /// node cannot be made the active one at all, so the gap is worth closing quickly.
    /// </summary>
    private static readonly TimeSpan RetryWhenUnknown = TimeSpan.FromMinutes(1);

    private string? _current;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private bool _warned;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        // An operator override wins and is taken verbatim: it is the escape hatch for a node whose
        // public address no echo service can see, such as one published through a static NAT or
        // reached over an internal split-horizon zone.
        if (options.PublicIpOverride is { } configured)
        {
            return configured;
        }

        if (options.PublicIpUrls.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextAttemptAt)
        {
            return _current;
        }

        foreach (var url in options.PublicIpUrls)
        {
            var discovered = await TryFetchAsync(url, cancellationToken);
            if (discovered is null)
            {
                continue;
            }

            if (!string.Equals(_current, discovered, StringComparison.Ordinal))
            {
                // Worth an info line: the panel's DNS record may now be pointing at an address
                // this node no longer holds, which is exactly the case an operator needs to see.
                logger.LogInformation("This node is reachable at {Address} (reported by {Service}).", discovered, url);
            }

            _current = discovered;
            _warned = false;
            _nextAttemptAt = now + options.PublicIpRefreshInterval;
            return _current;
        }

        _nextAttemptAt = now + (_current is null ? RetryWhenUnknown : options.PublicIpRefreshInterval);

        if (!_warned)
        {
            _warned = true;
            logger.LogWarning(
                "Could not determine this node's public address from any of {Count} service(s). "
                + "The panel cannot use it as a failover target until it can; set AWG_PUBLIC_IP to state it directly.",
                options.PublicIpUrls.Count);
        }

        // The last known answer stands. A flaky lookup is not a reason to tell the panel this
        // node has no address and drop it out of the failover rotation.
        return _current;
    }

    private async Task<string?> TryFetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(options.PublicIpTimeout);

            var body = await http.GetStringAsync(url, attempt.Token);
            if (PublicIpAddress.TryParse(body, out var address))
            {
                return address;
            }

            logger.LogDebug("{Service} did not answer with a public address.", url);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogDebug(exception, "Public address lookup via {Service} failed.", url);
        }

        return null;
    }
}
