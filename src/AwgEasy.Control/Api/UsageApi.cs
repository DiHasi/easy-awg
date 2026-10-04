using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Traffic over time, for the fleet and for one person.
///
/// The live counters next door answer "how much has this peer ever moved"; these answer "when",
/// which is what any question about a person - who is costing the most, who stopped using their
/// config, which evening the node was saturated - actually turns on.
///
/// Windows are a fixed set rather than a free from/to pair. The bucket width follows from the
/// window, so a caller cannot ask for ninety days of hourly points, and every answer is a few
/// hundred numbers whatever the size of the fleet.
/// </summary>
public static class UsageApi
{
    /// <param name="Span">How far back the window reaches.</param>
    /// <param name="Grain">How wide one point is. Hours past a week stop being readable as a shape.</param>
    private sealed record Window(string Key, TimeSpan Span, UsageGrain Grain);

    private static readonly Window[] Windows =
    [
        new("24h", TimeSpan.FromHours(24), UsageGrain.Hour),
        new("7d", TimeSpan.FromDays(7), UsageGrain.Hour),
        new("30d", TimeSpan.FromDays(30), UsageGrain.Day),
        new("90d", TimeSpan.FromDays(90), UsageGrain.Day)
    ];

    private static readonly Window Default = Windows[1];

    public static void MapUsage(this RouteGroupBuilder admin)
    {
        admin.MapGet("/usage", (
            string? window,
            UsageRepository usage,
            ClientRepository clients,
            ControlOptions options) =>
        {
            if (!TryRead(window, out var selected, out var error))
            {
                return Results.BadRequest(error);
            }

            var (from, to) = Range(selected, DateTimeOffset.UtcNow);
            var totals = usage.ByClient(from, to);
            var series = Densify(usage.Series(from, to, selected.Grain), from, to, selected.Grain);

            // Every peer the panel knows, in the arrangement the operator reads the list in, so a
            // person with nothing this week is visible as a zero rather than missing from the page.
            var perClient = clients.List()
                .Select(client => Usage(client, totals.GetValueOrDefault(client.Id)))
                .ToArray();

            return Results.Ok(new UsageSummaryResponse(
                selected.Key,
                Name(selected.Grain),
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
            ClientRepository clients,
            UsageRepository usage,
            ControlOptions options) =>
        {
            if (clients.Find(id) is null)
            {
                return Results.NotFound(new ApiError("client_not_found", "Client was not found."));
            }

            if (!TryRead(window, out var selected, out var error))
            {
                return Results.BadRequest(error);
            }

            var (from, to) = Range(selected, DateTimeOffset.UtcNow);
            var series = Densify(usage.Series(from, to, selected.Grain, id), from, to, selected.Grain);
            var totals = usage.ByClient(from, to).GetValueOrDefault(id);

            return Results.Ok(new ClientUsageSeriesResponse(
                id,
                selected.Key,
                Name(selected.Grain),
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

    private static bool TryRead(string? key, out Window window, out ApiError error)
    {
        window = Default;
        error = default!;

        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        if (Windows.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            error = new ApiError(
                "usage_window_invalid",
                $"Window must be one of {string.Join(", ", Windows.Select(candidate => candidate.Key))}.");

            return false;
        }

        window = found;
        return true;
    }

    /// <summary>
    /// The window, aligned to its own bucket width. The hour - or the day - in progress is the last
    /// bucket and is deliberately included: it is partial, and leaving it out would mean the page
    /// never shows what is happening right now.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To) Range(Window window, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var current = window.Grain == UsageGrain.Day
            ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);

        var to = current + Step(window.Grain);
        return (to - window.Span, to);
    }

    /// <summary>
    /// Fills the buckets nothing was recorded in. A series with holes in it would have to be
    /// gap-filled by every caller that draws it, and a chart that silently closes over a quiet
    /// night reads as a busy one.
    /// </summary>
    private static UsagePoint[] Densify(
        IReadOnlyList<UsageBucket> recorded,
        DateTimeOffset from,
        DateTimeOffset to,
        UsageGrain grain)
    {
        var step = Step(grain);
        var found = recorded.ToDictionary(bucket => bucket.At);
        var points = new List<UsagePoint>();

        for (var at = from; at < to; at += step)
        {
            var bucket = found.GetValueOrDefault(at);
            points.Add(new UsagePoint(at, bucket.Rx, bucket.Tx));
        }

        return [.. points];
    }

    private static TimeSpan Step(UsageGrain grain)
        => grain == UsageGrain.Day ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);

    private static string Name(UsageGrain grain) => grain == UsageGrain.Day ? "day" : "hour";
}
