using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <param name="Passed">Something answered through the tunnel.</param>
/// <param name="ProbeFault">The probe could not run the check at all; says nothing about the node.</param>
public sealed record TrafficResult(bool Passed, bool ProbeFault, int? LatencyMs, string? Detail, string? ObservedAddress = null);

/// <summary>
/// Fetches a resource through the probe's tunnel, after the handshake has completed.
///
/// A completed handshake is not enough. The blocking seen in practice lets the handshake through
/// and then drops everything after it: the node looks reachable, the client shows "connected", and
/// no page ever loads. Only data that goes out through the tunnel and comes back says a client
/// would actually be served - and it also covers a node whose NAT or egress is broken, which a
/// handshake cannot see either.
///
/// The request leaves through the probe interface because its socket is bound to it with
/// SO_BINDTODEVICE, not because of a route. The probe config sets `Table = off`, so the host's
/// routing is never touched, and Linux sends a device-bound socket out of that device even with
/// no route for the destination. The tunnel's AllowedIPs are 0.0.0.0/0, so the replies the node
/// sends back are accepted.
/// </summary>
public sealed class TunnelTrafficCheck(ILogger<TunnelTrafficCheck> logger)
{
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    public async Task<TrafficResult> CheckAsync(
        string interfaceName,
        IReadOnlyList<string> urls,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool checkSource = false,
        string? expectedSource = null)
    {
        string? lastFailure = null;
        string? observed = null;
        string? mismatch = null;
        int? mismatchLatency = null;
        var invalidEcho = false;

        // Any one answering is enough: the point is whether traffic flows, not whether a given
        // site is up, so a second address keeps one outage from reading as a blocked node.
        foreach (var url in urls)
        {
            using var handler = CreateHandler(interfaceName);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4096 };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            var clock = Stopwatch.StartNew();
            try
            {
                // Any status at all: a 404 or a redirect still came back through the tunnel.
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (checkSource)
                {
                    // Echo bodies are small. Loading with a limit also bounds a service that
                    // accidentally returns a page instead of an address.
                    await response.Content.LoadIntoBufferAsync(4096, deadline.Token);
                    var body = await response.Content.ReadAsStringAsync(deadline.Token);
                    var seen = ObservedAddress(body);
                    if (!response.IsSuccessStatusCode || seen is null)
                    {
                        invalidEcho = true;
                        lastFailure = $"{url}: did not return a source IPv4 address.";
                        continue;
                    }
                    observed = seen;
                    if (expectedSource is not null && observed != expectedSource)
                    {
                        mismatch = $"Traffic passed through the tunnel; seen as {observed}. The node reported {expectedSource} for this source address; different routing or NAT may explain the difference.";
                        mismatchLatency = (int)clock.ElapsedMilliseconds;
                        lastFailure = mismatch;
                        continue;
                    }
                    return new TrafficResult(true, false, (int)clock.ElapsedMilliseconds,
                        $"Traffic passed through the tunnel; seen from outside as {observed}.", observed);
                }
                return new TrafficResult(true, false, (int)clock.ElapsedMilliseconds, $"{url} answered {(int)response.StatusCode}.");
            }
            catch (Exception exception) when (FindProbeFault(exception) is { } fault)
            {
                return new TrafficResult(false, true, null, fault.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = $"{url}: no answer within {((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
            }
            catch (HttpRequestException exception)
            {
                lastFailure = $"{url}: {exception.InnerException?.Message ?? exception.Message}";
            }

            logger.LogDebug("Nothing came back through {InterfaceName}: {Failure}", interfaceName, lastFailure);
        }

        if (mismatch is not null)
            return new TrafficResult(true, false, mismatchLatency, mismatch, observed);
        return new TrafficResult(false, invalidEcho && observed is null, null, lastFailure, observed);
    }

    internal static string? ObservedAddress(string body)
    {
        if (EgressAddress.TryParse(body.Trim(), out var address)) return address;
        foreach (var line in body.Split('\n', StringSplitOptions.TrimEntries))
            if (line.StartsWith("ip=", StringComparison.Ordinal) && EgressAddress.TryParse(line[3..], out address)) return address;
        return null;
    }

    /// <summary>
    /// A fresh handler per check: a pooled connection would outlive the interface it was bound to,
    /// and the next check would either reuse a dead socket or skip the tunnel it is meant to test.
    /// </summary>
    private static SocketsHttpHandler CreateHandler(string interfaceName)
        => new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress[] addresses;
                try
                {
                    // Resolved by the probe host, outside the tunnel. A failure here is the probe's
                    // own network, not the node.
                    addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                }
                catch (SocketException exception)
                {
                    throw new ProbeFaultException($"Could not resolve {context.DnsEndPoint.Host} on the probe host: {exception.Message}");
                }

                if (addresses.Length == 0)
                {
                    throw new ProbeFaultException($"{context.DnsEndPoint.Host} has no IPv4 address; the tunnel carries IPv4 only.");
                }

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    try
                    {
                        socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(interfaceName + "\0"));
                    }
                    catch (SocketException exception)
                    {
                        // Without the binding the request would leave through the host's own route
                        // and test the probe's internet rather than the tunnel.
                        throw new ProbeFaultException($"Could not bind to {interfaceName} (needs CAP_NET_RAW): {exception.Message}");
                    }

                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

    private static ProbeFaultException? FindProbeFault(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProbeFaultException fault)
            {
                return fault;
            }
        }

        return null;
    }

    private sealed class ProbeFaultException(string message) : Exception(message);
}
