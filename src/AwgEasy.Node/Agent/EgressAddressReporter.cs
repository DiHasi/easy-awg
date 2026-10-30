using System.Net;
using System.Net.Sockets;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Builds the list of source addresses this node has to offer, for the status report the panel
/// fills its egress plan from.
///
/// The addresses themselves are re-read every report: an operator who has just added one on the
/// server should see it in the panel on the next poll rather than ten minutes later. What costs
/// something is asking the outside what each of them looks like, so that answer is kept and
/// refreshed on the same interval as the node's own public address.
///
/// Everything here is survivable and none of it touches the data plane: a node that cannot read
/// its addresses reports none, and the panel keeps the list it already had.
/// </summary>
public sealed class EgressAddressReporter(
    EgressInterfaceResolver egressResolver,
    EgressAddressResolver addressResolver,
    NodeOptions options,
    ILogger<EgressAddressReporter> logger)
{
    /// <summary>
    /// How long the whole pass over the addresses may take. The report is what tells the panel
    /// this node is alive, and a node that has been given four addresses the provider does not
    /// route outbound would otherwise hold it up for four timeouts in a row and read as silent.
    /// What does not fit is left unanswered until the next refresh.
    /// </summary>
    private static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, string?> _observed = new(StringComparer.Ordinal);
    private DateTimeOffset _nextObservationAt = DateTimeOffset.MinValue;

    public async Task<ReportedEgressAddress[]?> GetAsync(CancellationToken cancellationToken)
    {
        // The interface the last apply actually used, overrides and all. Resolving afresh would
        // detect the default-route one and describe the wrong interface on a node the panel has
        // pointed somewhere else.
        var egress = egressResolver.Current ?? await egressResolver.ResolveAsync(bundleOverride: null, cancellationToken);

        var set = await addressResolver.ListAsync(egress, cancellationToken);
        if (set is null)
        {
            // Null, not an empty list: the panel keeps what it had rather than forgetting a
            // node's addresses over one failed lookup.
            return null;
        }

        await RefreshObservationsAsync(set.Addresses, cancellationToken);

        return
        [
            .. set.Addresses.Select(address => new ReportedEgressAddress(
                address,
                string.Equals(address, set.KernelDefault, StringComparison.Ordinal),
                _observed.GetValueOrDefault(address)))
        ];
    }

    private async Task RefreshObservationsAsync(string[] addresses, CancellationToken cancellationToken)
    {
        // Discovery off means off for this too: the one switch an operator has for "this node
        // does not talk to echo services" has to cover every request to one.
        if (options.PublicIpUrls.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextObservationAt)
        {
            return;
        }

        _nextObservationAt = now + options.PublicIpRefreshInterval;

        var deadline = now + ObservationBudget;
        foreach (var address in addresses)
        {
            _observed[address] = DateTimeOffset.UtcNow < deadline
                ? await ObserveAsync(address, cancellationToken)
                : null;
        }

        // An address that is gone keeps nothing behind it.
        foreach (var stale in _observed.Keys.Where(known => !addresses.Contains(known, StringComparer.Ordinal)).ToArray())
        {
            _observed.Remove(stale);
        }
    }

    /// <summary>
    /// What an echo service answers when the request leaves with this address as its source.
    ///
    /// A handler of its own per address, because binding the source is a property of the socket
    /// and there is no other way to ask the question. These run once per refresh interval per
    /// address, so the cost of building one is not worth avoiding.
    /// </summary>
    private async Task<string?> ObserveAsync(string address, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out var source))
        {
            return null;
        }

        // One budget for this address rather than one per service, so a node with several
        // addresses cannot spend the whole report on the echo list of the first one.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(options.PublicIpTimeout);

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        using var http = new HttpClient(handler)
        {
            Timeout = options.PublicIpTimeout,
            MaxResponseContentBufferSize = 4096
        };

        foreach (var url in options.PublicIpUrls)
        {
            try
            {
                var body = await http.GetStringAsync(url, attempt.Token);
                if (PublicIpAddress.TryParse(body, out var seen))
                {
                    return seen;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Expected for an address the provider does not route outbound, which is itself
                // worth knowing: it is reported as observed-nothing rather than as an error.
                logger.LogDebug(exception, "Egress check from {Address} via {Service} failed.", address, url);
            }
        }

        return null;
    }
}
