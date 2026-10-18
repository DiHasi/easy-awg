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
