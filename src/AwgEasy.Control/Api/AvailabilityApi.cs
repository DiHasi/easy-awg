using System.Globalization;
using AwgEasy.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AwgEasy.Control;

/// <summary>
/// How the fleet has stood over time, per node and per probe.
///
/// The node cards answer what a node is right now, which is what matters while something is
/// burning. This answers how often it has been that, which is what decides the slower questions:
/// whether the node that just recovered has been flapping all week, which network a block is
/// actually in, and whether the next node is worth putting somewhere else entirely.
///
/// Read over the same windows as the traffic history, through <see cref="HistoryWindows"/>, and
/// the series is dense for the same reason: an hour nothing was recorded in is a gap in the
/// panel's own record, not an hour the fleet was down, and the two must not look alike.
/// </summary>
public static class AvailabilityApi
{
    /// <summary>What the window and offset error codes are about. Part of the API, so it is fixed.</summary>
    private const string Subject = "availability";

    public static void MapAvailability(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes/availability/checks", ListChecks)
            .WithName("ListAvailabilityChecks")
            .WithSummary("Read individual panel assessments and probe checks");

        // Under /nodes because it is the nodes page asking about its own nodes. A literal segment
        // beats the {id} routes beside it, so this is not one of them.
        admin.MapGet("/nodes/availability", (
            string? window,
            int? offset,
            AvailabilityRepository availability,
            NodeRepository nodes,
            ProbeRepository probes,
            ControlOptions options) =>
        {
            if (!HistoryWindows.TryRead(window, Subject, out var selected, out var error)
                || !HistoryWindows.TryReadOffset(offset, Subject, out var minutes, out error))
            {
                return Results.BadRequest(error);
            }

            var (from, to) = HistoryWindows.Range(selected, DateTimeOffset.UtcNow, minutes);
            var series = availability.Series(from, to, selected.Grain, minutes);
            var totals = availability.ByNode(from, to);
            var seen = availability.ByProbe(from, to).ToLookup(row => row.ProbeId, StringComparer.Ordinal);

            // Every node the panel knows, in the order the nodes page reads them, so a node with
            // nothing recorded is visible as "no history" rather than missing from the page.
            var perNode = nodes.List()
                .Select(node => Describe(
                    node,
                    totals.GetValueOrDefault(node.Id),
                    Densify(series.GetValueOrDefault(node.Id) ?? [], from, to, selected.Grain)))
                .ToArray();

            // Revoked probes included: what a probe saw while it was trusted is still what
            // happened, and the row says plainly that it is no longer counted.
            var perProbe = probes.List()
                .Select(probe => new ProbeAvailabilityResponse(
                    probe.Id,
                    probe.Name,
                    probe.Revoked,
                    [.. seen[probe.Id].Select(row => new ProbeNodeAvailabilityResponse(
                        row.NodeId,
                        row.Reachable,
                        row.Unreachable,
                        row.Errors,
                        row.Handshakes,
                        Share(row.Reachable, row.Reachable + row.Unreachable)))]))
                .ToArray();

            return Results.Ok(new AvailabilitySummaryResponse(
                selected.Key,
                HistoryWindows.Name(selected.Grain),
                from,
                to,
                perNode,
                perProbe,
                options.Availability.RetentionDays,
                availability.FirstRecordedAt()));
        });

        // Clear the journal and summaries together, for one node or for the fleet.
        //
        // Panel bookkeeping, like the traffic history: no revision moves and nothing reaches a
        // node. The states on the cards are judged from current evidence either way, so clearing
        // the history costs the panel nothing it needs in order to act.
        admin.MapPost("/nodes/availability/reset", (
            AvailabilityRepository availability,
            EventLog events,
            HttpContext context) =>
        {
            var dropped = availability.Clear();
            events.Record(
                "availability.reset",
                $"Availability history cleared for the whole fleet ({dropped} rows).",
                actor: context.User.Identity?.Name);

            return Results.Ok(new AvailabilityResetResponse(null, dropped));
        });

        admin.MapPost("/nodes/{id}/availability/reset", (
            string id,
            AvailabilityRepository availability,
            NodeRepository nodes,
            EventLog events,
            HttpContext context) =>
        {
            // Checked rather than quietly dropping nothing: an id that is gone means the operator
            // is looking at a page older than the fleet, and a cheerful "0 rows" would hide that.
            if (nodes.Find(id) is not { } node)
            {
                return Results.NotFound(new ApiError("node_not_found", "Node was not found."));
            }

            var dropped = availability.Clear(id);
            events.Record(
                "availability.reset",
                $"Availability history cleared for {node.Name} ({dropped} rows).",
                actor: context.User.Identity?.Name,
                nodeId: id);

            return Results.Ok(new AvailabilityResetResponse(id, dropped));
        });
    }

    private static Results<Ok<AvailabilityChecksResponse>, BadRequest<ApiError>> ListChecks(
        string? nodeId,
        string? kind,
        bool? issuesOnly,
        DateTimeOffset? until,
        string? cursor,
        AvailabilityCheckRepository checks,
        ControlOptions options)
    {
        if (kind is not (null or "panel" or "probe"))
        {
            return TypedResults.BadRequest(new ApiError("availability_kind_invalid", "Check kind must be panel or probe."));
        }

        const int pageSize = 50;
        var now = DateTimeOffset.UtcNow;
        var upper = until ?? DateTimeOffset.MaxValue;
        string? beforeId = null;
        if (cursor is not null)
        {
            var parts = cursor.Split('/', 2);
            if (parts.Length != 2
                || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                || !Guid.TryParseExact(parts[1], "N", out _)
                || at >= upper)
            {
                return TypedResults.BadRequest(new ApiError("availability_cursor_invalid", "Invalid check cursor."));
            }

            upper = at;
            beforeId = parts[1];
        }

        var retention = options.Availability.RetentionDays;
        if (!options.Availability.Records)
        {
            return TypedResults.Ok(new AvailabilityChecksResponse([], null, retention));
        }

        var rows = checks.List(now.AddDays(-retention), upper, beforeId, nodeId, kind, issuesOnly ?? false, pageSize + 1);
        var next = rows.Count > pageSize
            ? $"{HistoryWindows.Key(rows[pageSize - 1].Check.CheckedAt)}/{rows[pageSize - 1].Check.Id}"
            : null;
        var items = rows.Take(pageSize).Select(row => new AvailabilityCheckResponse(
            row.Check.Id,
            row.Check.CheckedAt,
            row.Check.NodeId,
            row.NodeName,
            row.Check.ProbeId,
            row.ProbeName,
            row.Check.Outcome,
            row.Check.Source,
            row.Check.Address,
            row.Check.Handshake,
            row.Check.LatencyMs,
            row.Check.Detail)).ToArray();

        return TypedResults.Ok(new AvailabilityChecksResponse(items, next, retention));
    }

    private static NodeAvailabilityResponse Describe(
        NodeRecord node,
        AvailabilityBucket totals,
        NodeAvailabilityPoint[] series)
        => new(
            node.Id,
            node.Name,
            node.Revoked,
            totals.Samples,
            totals.Healthy,
            totals.Blocked,
            totals.Down,
            totals.Silent,
            totals.Unknown,
            Share(totals.Healthy, totals.Samples),
            series);

    /// <summary>
    /// Fills the buckets nothing was recorded in with a zero-sample point, which is how a hole in
    /// the panel's own record is told apart from an hour the node was down: zero samples is "no
    /// evidence", not "never healthy". Everything that divides by samples has to keep that
    /// distinction, or a panel that was restarted reads as a fleet that was unavailable.
    /// </summary>
    private static NodeAvailabilityPoint[] Densify(
        IReadOnlyList<AvailabilityBucket> recorded,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryGrain grain)
    {
        var found = recorded.ToDictionary(bucket => bucket.At);

        return [.. HistoryWindows.Buckets(from, to, grain).Select(at =>
        {
            var bucket = found.GetValueOrDefault(at);
            return new NodeAvailabilityPoint(
                at,
                bucket.Samples,
                bucket.Healthy,
                bucket.Blocked,
                bucket.Down,
                bucket.Silent,
                bucket.Unknown);
        })];
    }

    /// <summary>
    /// A share of the samples taken, or null when none were. Null rather than zero because "no
    /// history" and "never once healthy" are opposite answers, and only one of them is about the
    /// node.
    /// </summary>
    private static double? Share(int part, int total) => total == 0 ? null : (double)part / total;
}
