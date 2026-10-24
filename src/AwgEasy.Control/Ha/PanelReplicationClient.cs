using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <param name="NotModified">The fleet has not changed since the ETag that was sent, so there is
/// nothing to apply. The common case by a wide margin.</param>
public sealed record PanelFetch<T>(T? Value, bool NotModified, string? Etag, ApiError? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The standby's side of replication: pulls from the panel in charge and reports back.
///
/// Requests are signed with this panel's own key, exactly as an agent signs its own - the private
/// key never crosses the wire, and unlike mTLS it survives a TLS-terminating proxy in front of
/// either panel. Everything that comes back is signed with the fleet signing key and verified
/// against the one pinned at enrollment, because a standby holds the whole fleet and must not
/// accept its state from whatever happens to answer the hostname it was pointed at.
/// </summary>
public sealed class PanelReplicationClient(HttpClient http, ILogger<PanelReplicationClient> logger)
{
    public async Task<(PanelEnrollResponse? Response, ApiError? Error)> EnrollAsync(
        string peerUrl,
        string token,
        string agentPublicKey,
        CancellationToken cancellationToken)
    {
        var request = new EnrollRequest(token, Environment.MachineName, agentPublicKey, PanelVersion.Current);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Url(peerUrl, "/api/v1/panels/enroll"))
            {
                Content = JsonContent.Create(request, ControlJsonContext.Default.EnrollRequest)
            };

            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await ReadErrorAsync(response, "panel_enrollment_failed", cancellationToken));
            }

            var enrolled = await response.Content.ReadFromJsonAsync(ControlJsonContext.Default.PanelEnrollResponse, cancellationToken);
            return enrolled is null
                ? (null, new ApiError("panel_enrollment_failed", "The panel in charge answered with an empty enrollment response."))
                : (enrolled, null);
        }
        catch (Exception exception) when (Transient(exception, cancellationToken))
        {
            return (null, new ApiError("panel_unreachable", $"Could not reach {peerUrl}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Fetches the configuration, gated by the ETag of the one already applied.
    ///
    /// The ETag travels as a header, which is outside the request signature. That is deliberate
    /// and harmless: the worst a tampered ETag can do is force a full transfer or withhold one,
    /// and anything on the path that can do the second can simply drop the request instead. What
    /// must not be forgeable is the content, and that is signed.
    /// </summary>
    public Task<PanelFetch<SignedPanelPayload>> FetchSnapshotAsync(
        string peerUrl,
        string panelId,
        string? etag,
        PanelLocalState identity,
        CancellationToken cancellationToken)
        => SendAsync(
            peerUrl,
            HttpMethod.Get,
            $"/api/v1/panels/{panelId}/snapshot",
            content: null,
            etag,
            identity,
            cancellationToken);

    /// <summary>
    /// Fetches the live half and the next slice of history.
    ///
    /// A POST for what is a read, because the cursors go in the body and the body is covered by
    /// the signature. On a query string they would sit outside it, and a cursor is what decides
    /// which history this panel is handed.
    /// </summary>
    public Task<PanelFetch<SignedPanelPayload>> FetchStreamAsync(
        string peerUrl,
        string panelId,
        PanelStreamCursors cursors,
        PanelLocalState identity,
        CancellationToken cancellationToken)
        => SendAsync(
            peerUrl,
            HttpMethod.Post,
            $"/api/v1/panels/{panelId}/stream",
            JsonContent.Create(cursors, ControlJsonContext.Default.PanelStreamCursors),
            etag: null,
            identity,
            cancellationToken);

    public async Task<(PanelHello? Hello, ApiError? Error)> ReportStatusAsync(
        string peerUrl,
        string panelId,
        PanelStatusReport report,
        PanelLocalState identity,
        CancellationToken cancellationToken)
    {
        var result = await SendRawAsync(
            peerUrl,
            HttpMethod.Post,
            $"/api/v1/panels/{panelId}/status",
            JsonContent.Create(report, ControlJsonContext.Default.PanelStatusReport),
            etag: null,
            identity,
            ControlJsonContext.Default.PanelHello,
            cancellationToken);

        return (result.Value, result.Error);
    }

    /// <summary>
    /// Asks a peer who it thinks is in charge. One cheap signed call, and the only thing the
    /// fencing needs: a panel that answers at a higher term has superseded the caller, and the
    /// caller steps down on arithmetic rather than on a judgement.
    /// </summary>
    public async Task<(PanelHello? Hello, ApiError? Error)> HelloAsync(
        string peerUrl,
        PanelLocalState identity,
        CancellationToken cancellationToken)
    {
        var result = await SendRawAsync(
            peerUrl,
            HttpMethod.Get,
            "/api/v1/panels/hello",
            content: null,
            etag: null,
            identity,
            ControlJsonContext.Default.PanelHello,
            cancellationToken);

        return (result.Value, result.Error);
    }

    private Task<PanelFetch<SignedPanelPayload>> SendAsync(
        string peerUrl,
        HttpMethod method,
        string path,
        HttpContent? content,
        string? etag,
        PanelLocalState identity,
        CancellationToken cancellationToken)
        => SendRawAsync(peerUrl, method, path, content, etag, identity, ControlJsonContext.Default.SignedPanelPayload, cancellationToken);

    private async Task<PanelFetch<T>> SendRawAsync<T>(
        string peerUrl,
        HttpMethod method,
        string path,
        HttpContent? content,
        string? etag,
        PanelLocalState identity,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(method, Url(peerUrl, path)) { Content = content };
            if (etag is not null)
            {
                message.Headers.TryAddWithoutValidation("If-None-Match", Quote(etag));
            }

            await SignAsync(message, path, identity, cancellationToken);

            using var response = await http.SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new PanelFetch<T>(default, true, etag, null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The peer does not accept this panel's identity any more: revoked, deleted, or
                // talking to a fleet it was never enrolled into. Retrying changes nothing, and
                // saying so plainly is more use than a generic failure every thirty seconds.
                return new PanelFetch<T>(default, false, null, new ApiError(
                    "panel_identity_rejected",
                    $"The panel at {peerUrl} refused this panel's identity with HTTP {(int)response.StatusCode} on {path}. "
                    + "It may have been revoked or removed there."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PanelFetch<T>(default, false, null, await ReadErrorAsync(response, "panel_sync_failed", cancellationToken));
            }

            var value = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
            return value is null
                ? new PanelFetch<T>(default, false, null, new ApiError("panel_sync_failed", "The panel in charge answered with an empty body."))
                : new PanelFetch<T>(value, false, Unquote(response.Headers.ETag?.Tag), null);
        }
        catch (Exception exception) when (Transient(exception, cancellationToken))
        {
            logger.LogDebug(exception, "Panel request to {PeerUrl}{Path} failed.", peerUrl, path);
            return new PanelFetch<T>(default, false, null, new ApiError("panel_unreachable", $"Could not reach {peerUrl}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Signs method, path, timestamp, nonce and a hash of the body with this panel's key. The path
    /// is signed without the query string because that is what the other side reconstructs - the
    /// reason the cursors travel in the body rather than on the URL.
    /// </summary>
    private static async Task SignAsync(
        HttpRequestMessage message,
        string path,
        PanelLocalState identity,
        CancellationToken cancellationToken)
    {
        var body = message.Content is null
            ? []
            : await message.Content.ReadAsByteArrayAsync(cancellationToken);

        var timestamp = AgentRequestSignature.Timestamp(DateTimeOffset.UtcNow);
        var nonce = AgentRequestSignature.NewNonce();
        var canonical = AgentRequestSignature.BuildCanonicalRequest(
            message.Method.Method,
            path,
            timestamp,
            nonce,
            body);

        using var key = BundleSigning.ImportPrivateKey(identity.AgentPrivateKey);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NodeHeader, identity.PanelId);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.TimestampHeader, timestamp);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NonceHeader, nonce);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.SignatureHeader, AgentRequestSignature.Sign(key, canonical));
    }

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response, string fallbackCode, CancellationToken cancellationToken)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync(ControlJsonContext.Default.ApiError, cancellationToken) is { Code.Length: > 0 } error)
            {
                return error;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            // Not every refusal is this panel's own shape - a proxy in front of it answers in
            // HTML. The status code is still the useful part.
        }

        return new ApiError(fallbackCode, $"The panel in charge answered with HTTP {(int)response.StatusCode}.");
    }

    private static bool Transient(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException or TaskCanceledException or TimeoutException or IOException or CryptographicException
            && !cancellationToken.IsCancellationRequested;

    private static Uri Url(string peerUrl, string path) => new($"{peerUrl.TrimEnd('/')}{path}");

    private static string Quote(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    private static string? Unquote(string? etag) => etag?.Trim('"');
}

/// <summary>
/// Whether this panel still has a working uplink, checked against something outside the pair.
///
/// It is the nearest thing to a witness a panel has. A node gets probes in other networks; a
/// standby gets this, and the difference between "the active is gone" and "my own network broke"
/// rests on it entirely. Any answer counts, from any of the URLs - the question is whether
/// packets leave this host at all, not whether a particular site is up.
/// </summary>
public interface IPanelWitness
{
    bool IsConfigured { get; }

    Task<bool> IsOnlineAsync(CancellationToken cancellationToken);
}

public sealed class HttpPanelWitness(
    IHttpClientFactory httpFactory,
    ControlOptions options,
    ILogger<HttpPanelWitness> logger) : IPanelWitness
{
    /// <summary>Its own client rather than a typed one: this is reached from the pair's long-lived
    /// loop, and a handler held for the life of the process would pin whatever these names
    /// resolved to on the day it started.</summary>
    public const string HttpClientName = "panel-witness";

    public bool IsConfigured => options.Panel.WitnessUrls.Count > 0;

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        using var http = httpFactory.CreateClient(HttpClientName);

        foreach (var url in options.Panel.WitnessUrls)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Panel.WitnessTimeout);

            try
            {
                // Any status at all is an answer: a 403 from a captive portal still proves the
                // uplink carries packets, which is the only thing being asked.
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Witness URL {Url} did not answer.", url);
            }
        }

        return false;
    }
}
