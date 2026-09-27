using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Points the failover record through the Cloudflare API.
///
/// The record is looked up by name rather than configured by id: an operator has the hostname in
/// front of them, not a 32-character record id, and a record that does not exist yet is created.
/// </summary>
public sealed class CloudflareDnsUpdater(HttpClient http, ControlOptions options, ILogger<CloudflareDnsUpdater> logger)
    : IDnsRecordUpdater
{
    public string ProviderName => "cloudflare";

    public bool IsConfigured => options.Dns.CloudflareConfigured;

    public async Task<DnsUpdateResult> PointAsync(DnsRecordTarget target, CancellationToken cancellationToken)
    {
        var zone = options.Dns.CloudflareZoneId;

        try
        {
            var existing = await FindRecordAsync(zone!, target, cancellationToken);

            // Never proxied. The orange cloud carries HTTP(S) only, so a proxied record would hand
            // clients Cloudflare's address and the tunnel would never come up - and the node's own
            // address, the thing failover moves, would stop being what the name resolves to.
            var body = new CloudflareRecordRequest(target.RecordType, target.Name, target.Address, target.Ttl, Proxied: false);

            using var request = existing is null
                ? new HttpRequestMessage(HttpMethod.Post, $"zones/{zone}/dns_records")
                : new HttpRequestMessage(HttpMethod.Patch, $"zones/{zone}/dns_records/{existing.Id}");

            request.Content = JsonContent.Create(body, ControlJsonContext.Default.CloudflareRecordRequest);

            using var response = await http.SendAsync(request, cancellationToken);
            var result = await ReadAsync(response, ControlJsonContext.Default.CloudflareRecordResponse, cancellationToken);

            if (!response.IsSuccessStatusCode || result?.Success != true)
            {
                return Failure(result?.Errors, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "Cloudflare rejected the API token. It needs Zone:DNS:Edit permission on this zone."
                    : $"Cloudflare returned {(int)response.StatusCode}.");
            }

            logger.LogInformation(
                "Cloudflare record {Name} {Type} now points at {Address} (ttl {Ttl}s).",
                target.Name,
                target.RecordType,
                target.Address,
                target.Ttl);

            return new DnsUpdateResult(
                DnsUpdateOutcome.Applied,
                existing is null
                    ? $"Created {target.Name} {target.RecordType} -> {target.Address}."
                    : $"Updated {target.Name} {target.RecordType} -> {target.Address}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Reaching Cloudflare is the one step of a switchover that can fail with nothing wrong
            // in the fleet itself, so it stays an error the operator reads rather than a 500.
            logger.LogWarning(exception, "Could not reach the Cloudflare API to move {Name}.", target.Name);
            return new DnsUpdateResult(
                DnsUpdateOutcome.Failed,
                Error: new ApiError("dns_update_failed", $"Could not reach the Cloudflare API: {exception.Message}"));
        }
    }

    private async Task<CloudflareRecord?> FindRecordAsync(string zone, DnsRecordTarget target, CancellationToken cancellationToken)
    {
        var query = $"zones/{zone}/dns_records?type={target.RecordType}&name={Uri.EscapeDataString(target.Name)}";

        using var response = await http.GetAsync(query, cancellationToken);
        var listed = await ReadAsync(response, ControlJsonContext.Default.CloudflareListResponse, cancellationToken);

        return listed?.Result?.FirstOrDefault();
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
        }
        catch (JsonException)
        {
            // A proxy error page where the API's JSON should be must not read as a successful edit.
            return default;
        }
    }

    private DnsUpdateResult Failure(CloudflareError[]? errors, string fallback)
    {
        var detail = errors is { Length: > 0 }
            ? string.Join("; ", errors.Select(error => $"{error.Code}: {error.Message}"))
            : fallback;

        logger.LogWarning("Cloudflare refused the DNS update: {Detail}", detail);
        return new DnsUpdateResult(DnsUpdateOutcome.Failed, Error: new ApiError("dns_update_failed", detail));
    }
}

// Only the fields this needs. Cloudflare's envelope has the same shape for every call: a success
// flag, an error list, and a result that is either one record or a page of them.
internal sealed record CloudflareRecordRequest(string Type, string Name, string Content, int Ttl, bool Proxied);

internal sealed record CloudflareRecord(string Id, string Name, string Type, string Content, int Ttl);

internal sealed record CloudflareError(int Code, string Message);

internal sealed record CloudflareListResponse(bool Success, CloudflareError[]? Errors, CloudflareRecord[]? Result);

internal sealed record CloudflareRecordResponse(bool Success, CloudflareError[]? Errors, CloudflareRecord? Result);
