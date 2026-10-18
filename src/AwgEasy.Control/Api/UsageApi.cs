using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Traffic over time, for the fleet and for one person.
///
/// The live counters next door answer "how much has this peer ever moved"; these answer "when",
/// which is what any question about a person - who is costing the most, who stopped using their
/// config, which evening the node was saturated - actually turns on.
///
/// The windows, and the arithmetic that turns one into a range of buckets, live in
/// <see cref="HistoryWindows"/>: the availability history is read over the same spans, and two
/// sets that drifted apart would make the same button mean different things on two pages.
/// </summary>
public static class UsageApi
{
    /// <summary>What the window and offset error codes are about. Part of the API, so it is fixed.</summary>
    private const string Subject = "usage";

    public static void MapUsage(this RouteGroupBuilder admin)
    {
        admin.MapGet("/usage", (
            string? window,
            int? offset,
            UsageRepository usage,
            ClientRepository clients,
            ControlOptions options) =>
        {
            if (!HistoryWindows.TryRead(window, Subject, out var selected, out var error)
                || !HistoryWindows.TryReadOffset(offset, Subject, out var minutes, out error))
            {
                return Results.BadRequest(error);
            }

            var (from, to) = HistoryWindows.Range(selected, DateTimeOffset.UtcNow, minutes);
            var totals = usage.ByClient(from, to, minutes);
            var series = Densify(usage.Series(from, to, selected.Grain, offsetMinutes: minutes), from, to, selected.Grain);

            // Every peer the panel knows, in the arrangement the operator reads the list in, so a
            // person with nothing this week is visible as a zero rather than missing from the page.
            var perClient = clients.List()
                .Select(client => Usage(client, totals.GetValueOrDefault(client.Id)))
                .ToArray();

            return Results.Ok(new UsageSummaryResponse(
                selected.Key,
                HistoryWindows.Name(selected.Grain),
                from,
                to,
                series.Sum(point => point.ReceivedBytes),
                series.Sum(point => point.TransmittedBytes),
                series,
                perClient,
                options.Usage.RetentionDays,
                usage.FirstRecordedAt()));
        });

        // Under /clients because it is one client's history, and because the peer sheet asks for it
        // by id the moment the traffic dialog opens.
        admin.MapGet("/clients/{id}/usage", (
            string id,
            string? window,
            int? offset,
            ClientRepository clients,
            UsageRepository usage,
            ControlOptions options) =>
        {
            if (clients.Find(id) is null)
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            if (!HistoryWindows.TryRead(window, Subject, out var selected, out var error)
                || !HistoryWindows.TryReadOffset(offset, Subject, out var minutes, out error))
            {
                return Results.BadRequest(error);
            }

            var (from, to) = HistoryWindows.Range(selected, DateTimeOffset.UtcNow, minutes);
            var series = Densify(usage.Series(from, to, selected.Grain, id, minutes), from, to, selected.Grain);
            var totals = usage.ByClient(from, to, minutes).GetValueOrDefault(id);

            return Results.Ok(new ClientUsageSeriesResponse(
                id,
                selected.Key,
                HistoryWindows.Name(selected.Grain),
                from,
                to,
                series.Sum(point => point.ReceivedBytes),
                series.Sum(point => point.TransmittedBytes),
                totals.ActiveDays,
                series,
                options.Usage.RetentionDays,
                usage.FirstRecordedAt()));
        });
    }

    private static ClientUsageResponse Usage(ClientRecord client, ClientUsageTotals totals)
        => new(
            client.Id,
            client.Name,
            client.GroupId,
            client.Enabled,
            totals.Rx,
            totals.Tx,
            totals.ActiveDays,
            totals.LastActiveAt);

    /// <summary>
    /// Fills the buckets nothing was recorded in. A series with holes in it would have to be
    /// gap-filled by every caller that draws it, and a chart that silently closes over a quiet
    /// night reads as a busy one.
    /// </summary>
    private static UsagePoint[] Densify(
        IReadOnlyList<UsageBucket> recorded,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryGrain grain)
    {
        var found = recorded.ToDictionary(bucket => bucket.At);

        return [.. HistoryWindows.Buckets(from, to, grain).Select(at =>
        {
            var bucket = found.GetValueOrDefault(at);
            return new UsagePoint(at, bucket.Rx, bucket.Tx);
        })];
    }
}
