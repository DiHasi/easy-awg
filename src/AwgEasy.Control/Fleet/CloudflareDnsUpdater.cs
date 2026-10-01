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
///
/// Looking up by name means the lookup has to be unambiguous, so every switch first checks that
/// the name belongs to the configured zone and that exactly one record could be the one clients
/// follow. Picking "the first" of several would move one record and leave clients on another - a
/// switch that reports success and moves nobody, which is the worst thing failover can do.
/// </summary>
public sealed class CloudflareDnsUpdater(HttpClient http, ControlOptions options, ILogger<CloudflareDnsUpdater> logger)
    : IDnsRecordUpdater
{
    public string ProviderName => "cloudflare";

    public bool IsConfigured => options.Dns.CloudflareConfigured;

    public async Task<DnsUpdateResult> PointAsync(DnsRecordTarget target, CancellationToken cancellationToken)
    {
        var zone = options.Dns.CloudflareZoneId!;

        try
        {
            var (existing, refusal) = await FindRecordAsync(zone, target, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

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
                return Failure("dns_update_failed", result?.Errors, Rejected(response, $"Cloudflare returned {(int)response.StatusCode}."));
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

    public async Task<ApiError?> CheckAsync(string recordName, CancellationToken cancellationToken)
    {
        try
        {
            var (_, refusal) = await InspectAsync(options.Dns.CloudflareZoneId!, recordName, cancellationToken);
            return refusal?.Error;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ApiError("dns_update_failed", $"Could not reach the Cloudflare API: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the one record to edit, or explains why there is not exactly one. A null record with
    /// no refusal means the name has no record yet and one is created.
    /// </summary>
    private async Task<(CloudflareRecord? Record, DnsUpdateResult? Refusal)> FindRecordAsync(
        string zone,
        DnsRecordTarget target,
        CancellationToken cancellationToken)
    {
        var (records, refusal) = await InspectAsync(zone, target.Name, cancellationToken);
        if (refusal is not null)
        {
            return (null, refusal);
        }

        // A node reports one address, so a record of the other family would keep answering with
        // wherever it pointed before: every client that prefers it stays on the old node.
        var otherFamily = target.RecordType == "A" ? "AAAA" : "A";
        if (records.FirstOrDefault(record => IsType(record, otherFamily)) is { } other)
        {
            return (null, Failure(
                "dns_record_conflict",
                null,
                $"{target.Name} has an {otherFamily} record ({other.Content}) that failover would not move, so clients "
                + $"that prefer {otherFamily} would stay where they are. Remove it, or give the node an address of that family."));
        }

        return (records.SingleOrDefault(record => IsType(record, target.RecordType)), null);
    }

    /// <summary>
    /// Everything that has to hold before a record under this name can be moved, whichever node
    /// it is moved to: the name is in the configured zone, and it answers through at most one
    /// address record.
    /// </summary>
    private async Task<(CloudflareRecord[] Records, DnsUpdateResult? Refusal)> InspectAsync(
        string zone,
        string name,
        CancellationToken cancellationToken)
    {
        using (var zoneResponse = await http.GetAsync($"zones/{zone}", cancellationToken))
        {
            var zoneResult = await ReadAsync(zoneResponse, ControlJsonContext.Default.CloudflareZoneResponse, cancellationToken);
            if (!zoneResponse.IsSuccessStatusCode || zoneResult?.Success != true || zoneResult.Result is null)
            {
                return ([], Failure(
                    "dns_zone_unreadable",
                    zoneResult?.Errors,
                    Rejected(zoneResponse, $"Cloudflare returned {(int)zoneResponse.StatusCode} for zone {zone}. Check AWG_CLOUDFLARE_ZONE_ID.")));
            }

            // A zone id copied from another zone produces a record nothing delegates to, and every
            // switch "succeeds" into the void.
            if (!BelongsTo(name, zoneResult.Result.Name))
            {
                return ([], Failure(
                    "dns_zone_mismatch",
                    null,
                    $"{name} is not in the Cloudflare zone {zoneResult.Result.Name} (AWG_CLOUDFLARE_ZONE_ID). Point "
                    + "AWG_CLOUDFLARE_ZONE_ID at the zone that holds the endpoint host, or set AWG_DNS_RECORD_NAME to a name inside this one."));
            }
        }

        // Every type under the name, not just the one being written: a record of another type is
        // what decides whether clients actually follow this one.
        using var response = await http.GetAsync(
            $"zones/{zone}/dns_records?name={Uri.EscapeDataString(name)}&per_page=100",
            cancellationToken);
        var listed = await ReadAsync(response, ControlJsonContext.Default.CloudflareListResponse, cancellationToken);

        // An unreadable listing is not an empty one. Treating it as empty would create a second
        // record beside the existing one, and the name would then answer with both nodes at once.
        if (!response.IsSuccessStatusCode || listed?.Success != true || listed.Result is null)
        {
            return ([], Failure(
                "dns_update_failed",
                listed?.Errors,
                Rejected(response, $"Cloudflare returned {(int)response.StatusCode} when listing records for {name}.")));
        }

        var records = listed.Result;

        if (records.FirstOrDefault(record => IsType(record, "CNAME")) is { } alias)
        {
            return ([], Failure(
                "dns_record_conflict",
                null,
                $"{name} is a CNAME to {alias.Content}. Failover moves an address record, so either set "
                + "AWG_DNS_RECORD_NAME to the name the CNAME targets, or replace the CNAME with an address record."));
        }

        foreach (var type in (string[])["A", "AAAA"])
        {
            var matching = records.Where(record => IsType(record, type)).ToArray();
            if (matching.Length > 1)
            {
                // Round-robin across several nodes is exactly what failover is not, and which one
                // Cloudflare happened to list first says nothing about which one clients use.
                return ([], Failure(
                    "dns_record_ambiguous",
                    null,
                    $"{name} has {matching.Length} {type} records ({string.Join(", ", matching.Select(record => record.Content))}). "
                    + "Failover moves one record; delete the others so the name answers with a single node."));
            }
        }

        if (records.Any(record => IsType(record, "A")) && records.Any(record => IsType(record, "AAAA")))
        {
            return ([], Failure(
                "dns_record_conflict",
                null,
                $"{name} has both an A and an AAAA record. A node reports one address, so a switch would move one of "
                + "them and leave clients that prefer the other on the old node. Keep one."));
        }

        return (records, null);
    }

    private static bool IsType(CloudflareRecord record, string type)
        => string.Equals(record.Type, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>The zone apex itself, or any name under it.</summary>
    internal static bool BelongsTo(string name, string zone)
    {
        var host = name.TrimEnd('.');
        var apex = zone.TrimEnd('.');
        return string.Equals(host, apex, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + apex, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rejected(HttpResponseMessage response, string fallback)
        => response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? "Cloudflare rejected the API token. It needs Zone:Read and Zone:DNS:Edit permission on this zone."
            : fallback;

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

    private DnsUpdateResult Failure(string code, CloudflareError[]? errors, string fallback)
    {
        var detail = errors is { Length: > 0 }
            ? string.Join("; ", errors.Select(error => $"{error.Code}: {error.Message}"))
            : fallback;

        logger.LogWarning("Cloudflare DNS update refused ({Code}): {Detail}", code, detail);
        return new DnsUpdateResult(DnsUpdateOutcome.Failed, Error: new ApiError(code, detail));
    }
}

// Only the fields this needs. Cloudflare's envelope has the same shape for every call: a success
// flag, an error list, and a result that is either one object or a page of them.
internal sealed record CloudflareRecordRequest(string Type, string Name, string Content, int Ttl, bool Proxied);

internal sealed record CloudflareRecord(string Id, string Name, string Type, string Content, int Ttl);

internal sealed record CloudflareZone(string Id, string Name);

internal sealed record CloudflareError(int Code, string Message);

internal sealed record CloudflareListResponse(bool Success, CloudflareError[]? Errors, CloudflareRecord[]? Result);

internal sealed record CloudflareRecordResponse(bool Success, CloudflareError[]? Errors, CloudflareRecord? Result);

internal sealed record CloudflareZoneResponse(bool Success, CloudflareError[]? Errors, CloudflareZone? Result);
