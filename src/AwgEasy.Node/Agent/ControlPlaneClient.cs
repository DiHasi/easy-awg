using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Talks to the control plane. The node opens no inbound ports of its own: every exchange is
/// outbound, so the agent works behind NAT and keeps its attack surface to the AmneziaWG port.
///
/// Requests are authenticated by signing them with the agent key rather than sending a bearer
/// token. The private key never crosses the wire, and unlike mTLS this survives any TLS-
/// terminating proxy or CDN in front of the control plane. Transport auth is deliberately
/// confined to <see cref="SignRequest"/> so it can be swapped for mTLS without touching
/// anything else.
/// </summary>
public sealed class ControlPlaneClient(HttpClient http, NodeOptions options, ILogger<ControlPlaneClient> logger)
{
    public async Task<EnrollResponse?> EnrollAsync(AgentIdentityDocument identity, string enrollmentToken, CancellationToken cancellationToken)
    {
        var request = new EnrollRequest(
            enrollmentToken,
            Environment.MachineName,
            identity.PublicKey,
            AgentVersion.Current);

        using var message = new HttpRequestMessage(HttpMethod.Post, Url("/api/v1/agents/enroll"))
        {
            Content = JsonContent.Create(request, NodeJsonContext.Default.EnrollRequest)
        };

        var response = await http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Enrollment rejected with {StatusCode}: {Body}", (int)response.StatusCode, await SafeReadAsync(response, cancellationToken));
            return null;
        }

        return await response.Content.ReadFromJsonAsync(NodeJsonContext.Default.EnrollResponse, cancellationToken);
    }

    /// <summary>
    /// Fetches the desired state. Returns null when the control plane reports the node is already
    /// at the current revision (304), so an unchanged fleet costs one cheap request per poll.
    /// </summary>
    public async Task<SignedBundle?> FetchBundleAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Url($"/api/v1/agents/{identity.NodeId}/desired"));
        message.Headers.TryAddWithoutValidation("If-None-Match", $"\"rev-{identity.AppliedRevision}\"");
        await SignRequestAsync(message, identity, cancellationToken);

        var response = await http.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Control plane returned {StatusCode} for the desired state.", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync(NodeJsonContext.Default.SignedBundle, cancellationToken);
    }

    public async Task ReportStatusAsync(AgentIdentityDocument identity, NodeStatusReport report, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Url($"/api/v1/agents/{identity.NodeId}/status"))
        {
            Content = JsonContent.Create(report, NodeJsonContext.Default.NodeStatusReport)
        };

        await SignRequestAsync(message, identity, cancellationToken);

        var response = await http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Status report rejected with {StatusCode}.", (int)response.StatusCode);
        }
    }

    /// <summary>
    /// Signs the request with the agent key. Body-covering, so a proxy cannot alter a status
    /// report in flight; the timestamp and nonce let the control plane refuse replays.
    /// </summary>
    private static async Task SignRequestAsync(HttpRequestMessage message, AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        var body = message.Content is null
            ? []
            : await message.Content.ReadAsByteArrayAsync(cancellationToken);

        var timestamp = AgentRequestSignature.Timestamp(DateTimeOffset.UtcNow);
        var nonce = AgentRequestSignature.NewNonce();
        var canonical = AgentRequestSignature.BuildCanonicalRequest(
            message.Method.Method,
            message.RequestUri?.AbsolutePath ?? "/",
            timestamp,
            nonce,
            body);

        using var key = BundleSigning.ImportPrivateKey(identity.PrivateKey);

        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NodeHeader, identity.NodeId ?? string.Empty);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.TimestampHeader, timestamp);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NonceHeader, nonce);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.SignatureHeader, AgentRequestSignature.Sign(key, canonical));
    }

    private Uri Url(string path) => new(new Uri(options.ControlUrl!.TrimEnd('/') + "/"), path.TrimStart('/'));

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return "<unreadable>";
        }
    }
}

public static class AgentVersion
{
    public static string Current { get; } =
        typeof(AgentVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
