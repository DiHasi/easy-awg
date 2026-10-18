using System.Globalization;
using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>
/// One bucket of a node's availability. The counters are samples, so <paramref name="Samples"/> is
/// the denominator of every share taken from the rest.
/// </summary>
public readonly record struct AvailabilityBucket(
    DateTimeOffset At,
    int Samples,
    int Healthy,
    int Blocked,
    int Down,
    int Silent,
    int Unknown);

/// <summary>What one probe saw of one node over a window.</summary>
public readonly record struct ProbeAvailabilityTotals(
    string ProbeId,
    string NodeId,
    int Reachable,
    int Unreachable,
    int Errors,
    int Handshakes);

/// <summary>
/// Reads the availability history back, and keeps it from growing without end.
///
/// The write side is two calls that each sit where the evidence is produced:
/// <see cref="Accrue"/> in the failover round that assessed the fleet, and
/// <see cref="AccrueProbeRound"/> inside the transaction that records a probe round. Everything
/// here aggregates in SQLite rather than in memory, like the traffic history next door: ninety
/// days of half-minute rounds over a fleet is a six-figure row count, and what the panel draws is
/// a few hundred numbers.
/// </summary>
public sealed class AvailabilityRepository(Database database)
{
    /// <summary>
    /// Credits one round to the hour it ran in: one sample per node, in the state the round found
    /// it. A revoked node is skipped - the panel cut it off on purpose, and recording that it is
    /// unreachable would be measuring its own decision.
    /// </summary>
    public void Accrue(IEnumerable<NodeAssessment> assessments, DateTimeOffset at)
    {
        var bucket = HistoryWindows.Key(HistoryWindows.Bucket(at));
        var now = HistoryWindows.Key(at);

        using var connection = database.Open();
        // BEGIN IMMEDIATE: this writes from its first statement, and status reports from every node
        // are writing alongside it. A deferred transaction that had to upgrade mid-way is the one
        // thing busy_timeout does not retry under WAL.
        using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var assessment in assessments)
        {
            if (Column(assessment.State) is not { } column)
            {
                continue;
            }

            // The state column and the sample counter move together, so a share can never come
            // out above 1 even if a round is somehow counted twice.
            using var upsert = connection.Sql(
                $"""
                INSERT INTO node_availability (node_id, bucket_start, samples, {column}, updated_at)
                SELECT $nodeId, $bucket, 1, 1, $now
                 WHERE EXISTS (SELECT 1 FROM nodes WHERE id = $nodeId)
                ON CONFLICT (node_id, bucket_start) DO UPDATE
                   SET samples    = samples + 1,
                       {column}   = {column} + 1,
                       updated_at = excluded.updated_at
                """,
                ("$nodeId", assessment.NodeId),
                ("$bucket", bucket),
                ("$now", now));

            upsert.Transaction = transaction;
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Credits one probe round, inside the transaction that stores its results.
    ///
    /// Written here rather than from a reader for the same reason the traffic history is written
    /// inside <see cref="NodeRepository.ReplacePeerStats"/>: a round is only ever reported once,
    /// and a second pass over the latest-result rows would miss every round that a later one
    /// overwrote before the pass ran.
    /// </summary>
    public static void AccrueProbeRound(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string probeId,
        ProbeResult result,
        DateTimeOffset at)
    {
        var column = result.Outcome switch
        {
            ProbeOutcomes.Reachable => "reachable",
            ProbeOutcomes.Unreachable => "unreachable",
            ProbeOutcomes.Error => "errors",
            _ => null
        };

        if (column is null)
        {
            return;
        }

        // A handshake counts whenever the probe saw one, reachable or not: the pair of numbers is
        // what separates "the address is blocked" from "the handshake goes through and nothing
        // follows it", which is the distinction the whole probe exists to draw.
        var handshake = result.Handshake == true ? 1 : 0;

        using var upsert = connection.Sql(
            $"""
            INSERT INTO probe_availability (probe_id, node_id, bucket_start, {column}, handshakes, updated_at)
            SELECT $probeId, $nodeId, $bucket, 1, $handshake, $now
             WHERE EXISTS (SELECT 1 FROM nodes WHERE id = $nodeId)
            ON CONFLICT (probe_id, node_id, bucket_start) DO UPDATE
               SET {column}   = {column} + 1,
                   handshakes = handshakes + excluded.handshakes,
                   updated_at = excluded.updated_at
            """,
            ("$probeId", probeId),
            ("$nodeId", result.NodeId),
            ("$bucket", HistoryWindows.Key(HistoryWindows.Bucket(at))),
            ("$handshake", handshake),
            ("$now", HistoryWindows.Key(at)));

        upsert.Transaction = transaction;
        upsert.ExecuteNonQuery();
    }

    /// <summary>
    /// Availability per bucket, per node. Buckets nothing was recorded in are simply absent;
    /// filling the gaps is the caller's business, because only it knows the window it asked about.
    /// </summary>
    /// <param name="offsetMinutes">
    /// The reader's offset east of UTC, which decides where one day ends and the next begins. It
    /// does nothing to an hourly series, exactly as in the traffic history: the rows sit on UTC
    /// hour boundaries, so there is no local hour boundary to rebuild out of them.
    /// </param>
    public IReadOnlyDictionary<string, List<AvailabilityBucket>> Series(
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryGrain grain,
        int offsetMinutes = 0)
    {
        var bucket = HistoryWindows.BucketExpression(grain);

        using var connection = database.Open();
        using var command = connection.Sql(
            $"""
            SELECT node_id,
                   {bucket}      AS bucket,
                   SUM(samples)  AS samples,
                   SUM(healthy)  AS healthy,
                   SUM(blocked)  AS blocked,
                   SUM(down)     AS down,
                   SUM(silent)   AS silent,
                   SUM(unknown)  AS unknown
              FROM node_availability
             WHERE bucket_start >= $from
               AND bucket_start <  $to
             GROUP BY node_id, bucket
             ORDER BY node_id, bucket
            """,
            ("$from", HistoryWindows.Key(from)),
            ("$to", HistoryWindows.Key(to)),
            ("$shift", HistoryWindows.Shift(offsetMinutes)));

        using var reader = command.ExecuteReader();

        var series = new Dictionary<string, List<AvailabilityBucket>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var nodeId = reader.GetString("node_id");
            if (!series.TryGetValue(nodeId, out var buckets))
            {
                buckets = [];
                series[nodeId] = buckets;
            }

            buckets.Add(new AvailabilityBucket(
                HistoryWindows.ParseBucket(reader.GetString("bucket"), grain, offsetMinutes),
                reader.GetInt32("samples"),
                reader.GetInt32("healthy"),
                reader.GetInt32("blocked"),
                reader.GetInt32("down"),
                reader.GetInt32("silent"),
                reader.GetInt32("unknown")));
        }

        return series;
    }

    /// <summary>
    /// What each node added up to over the window. Nodes with nothing recorded are absent, which
    /// is not the same as a node that was never up: the caller knows which nodes exist.
    /// </summary>
    public IReadOnlyDictionary<string, AvailabilityBucket> ByNode(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT node_id,
                   SUM(samples) AS samples,
                   SUM(healthy) AS healthy,
                   SUM(blocked) AS blocked,
                   SUM(down)    AS down,
                   SUM(silent)  AS silent,
                   SUM(unknown) AS unknown
              FROM node_availability
             WHERE bucket_start >= $from
               AND bucket_start <  $to
             GROUP BY node_id
            """,
            ("$from", HistoryWindows.Key(from)),
            ("$to", HistoryWindows.Key(to)));

        using var reader = command.ExecuteReader();

        var totals = new Dictionary<string, AvailabilityBucket>(StringComparer.Ordinal);
        while (reader.Read())
        {
            totals[reader.GetString("node_id")] = new AvailabilityBucket(
                from,
                reader.GetInt32("samples"),
                reader.GetInt32("healthy"),
                reader.GetInt32("blocked"),
                reader.GetInt32("down"),
                reader.GetInt32("silent"),
                reader.GetInt32("unknown"));
        }

        return totals;
    }

    /// <summary>What every probe saw of every node over the window: the per-network breakdown.</summary>
    public IReadOnlyList<ProbeAvailabilityTotals> ByProbe(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT probe_id,
                   node_id,
                   SUM(reachable)   AS reachable,
                   SUM(unreachable) AS unreachable,
                   SUM(errors)      AS errors,
                   SUM(handshakes)  AS handshakes
              FROM probe_availability
             WHERE bucket_start >= $from
               AND bucket_start <  $to
             GROUP BY probe_id, node_id
            """,
            ("$from", HistoryWindows.Key(from)),
            ("$to", HistoryWindows.Key(to)));

        using var reader = command.ExecuteReader();

        var totals = new List<ProbeAvailabilityTotals>();
        while (reader.Read())
        {
            totals.Add(new ProbeAvailabilityTotals(
                reader.GetString("probe_id"),
                reader.GetString("node_id"),
                reader.GetInt32("reachable"),
                reader.GetInt32("unreachable"),
                reader.GetInt32("errors"),
                reader.GetInt32("handshakes")));
        }

        return totals;
    }

    /// <summary>
    /// The oldest bucket still kept, so the panel can say how far back its own numbers go rather
    /// than drawing an empty month as if every node had been down through it.
    /// </summary>
    public DateTimeOffset? FirstRecordedAt()
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT MIN(bucket_start) FROM (
                SELECT MIN(bucket_start) AS bucket_start FROM node_availability
                UNION ALL
                SELECT MIN(bucket_start) AS bucket_start FROM probe_availability
            )
            """);

        var value = command.ExecuteScalar();
        return value is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    /// <summary>A node that leaves the fleet takes its own history, and what probes said about it,
    /// along - the same rule as its latest probe results.</summary>
    public void ForgetNode(string nodeId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var table in (string[])["node_availability", "probe_availability"])
        {
            using var delete = connection.Sql($"DELETE FROM {table} WHERE node_id = $nodeId", ("$nodeId", nodeId));
            delete.Transaction = transaction;
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>A deleted probe stops being a vantage point, so its column disappears with it.</summary>
    public void ForgetProbe(string probeId)
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM probe_availability WHERE probe_id = $probeId", ("$probeId", probeId));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Enforces retention. Also sweeps rows about nodes and probes that are gone, which catches
    /// the paths that delete wholesale.
    /// </summary>
    /// <returns>How many rows were dropped.</returns>
    public int Prune(DateTimeOffset before)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var dropped = 0;
        foreach (var table in (string[])["node_availability", "probe_availability"])
        {
            using var expired = connection.Sql(
                $"DELETE FROM {table} WHERE bucket_start < $before",
                ("$before", HistoryWindows.Key(before)));

            expired.Transaction = transaction;
            dropped += expired.ExecuteNonQuery();

            using var orphans = connection.Sql($"DELETE FROM {table} WHERE node_id NOT IN (SELECT id FROM nodes)");
            orphans.Transaction = transaction;
            dropped += orphans.ExecuteNonQuery();
        }

        using (var goneProbes = connection.Sql("DELETE FROM probe_availability WHERE probe_id NOT IN (SELECT id FROM probes)"))
        {
            goneProbes.Transaction = transaction;
            dropped += goneProbes.ExecuteNonQuery();
        }

        transaction.Commit();
        return dropped;
    }

    /// <summary>
    /// Which column a round increments. Unknown - a node that has not reported yet - is recorded
    /// rather than skipped, because a node nothing vouches for is honestly not healthy and the
    /// denominator has to say so. Revoked is not a state of availability at all.
    /// </summary>
    private static string? Column(string state) => state switch
    {
        NodeHealthStates.Healthy => "healthy",
        NodeHealthStates.Blocked => "blocked",
        NodeHealthStates.Down => "down",
        NodeHealthStates.Silent => "silent",
        NodeHealthStates.Unknown => "unknown",
        _ => null
    };
}
