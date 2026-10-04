using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>
/// Turns the counters on a status report into traffic credited to an hour.
///
/// A node reports totals, not deltas, so what moved since the last report is the difference
/// between two readings. That makes the previous reading load-bearing: it is the <c>peer_stats</c>
/// row the same report is about to overwrite, which is why this is written by
/// <see cref="NodeRepository.ReplacePeerStats"/> inside that one transaction rather than by the
/// repository that reads it back. A crash between the two halves would either lose an interval or
/// count it twice.
/// </summary>
internal static class UsageAccounting
{
    /// <summary>
    /// The hour a reading is credited to, in UTC. Reports arrive every 20 seconds or so, so the
    /// whole interval lands in the hour the later reading arrived in rather than being split
    /// across the boundary - an error bounded by one report interval, against a bucket of an hour.
    /// </summary>
    public static DateTimeOffset Bucket(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// What moved between two readings of the same counter.
    ///
    /// A reading below the one before it means the device started counting again - the node
    /// rebooted, or the interface was brought down - so everything it now reads is traffic since
    /// that restart. The same reasoning as <c>NodeRepository.SinceReset</c>, for the same reason:
    /// the kernel's counters are not monotonic across a device's lifetime.
    /// </summary>
    public static long Moved(long previous, long current)
        => current < previous ? current : current - previous;

    /// <summary>
    /// Credits what every reported peer moved since its last reading to the bucket
    /// <paramref name="reportedAt"/> falls in.
    /// </summary>
    /// <remarks>
    /// A peer with no previous reading is skipped rather than credited in full: its counter may
    /// have been climbing since long before this panel first heard of the node, and dropping a
    /// node's lifetime total into the hour it enrolled in would be a spike that never happened.
    /// A peer this panel does not know - a key a node still carries for a client since deleted,
    /// or a probe's, which is not a person's - is skipped too.
    /// </remarks>
    public static void Accrue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        PeerStatus[] peers,
        DateTimeOffset reportedAt)
    {
        if (peers.Length == 0)
        {
            return;
        }

        var previous = ReadPreviousCounters(connection, transaction, nodeId);
        if (previous.Count == 0)
        {
            return;
        }

        var clients = ReadClientKeys(connection, transaction);
        var bucket = Bucket(reportedAt).ToStorage();
        var now = reportedAt.ToUniversalTime().ToStorage();

        foreach (var peer in peers)
        {
            if (!previous.TryGetValue(peer.PublicKey, out var seen)
                || !clients.TryGetValue(peer.PublicKey, out var clientId))
            {
                continue;
            }

            var rx = Moved(seen.Rx, peer.ReceivedBytes);
            var tx = Moved(seen.Tx, peer.TransmittedBytes);
            if (rx == 0 && tx == 0)
            {
                // Nothing moved. Writing the row anyway would fill the table with zeroes for
                // every disabled and every idle peer, every hour, forever.
                continue;
            }

            using var insert = connection.Sql(
                """
                INSERT INTO client_usage (client_id, bucket_start, received_bytes, transmitted_bytes, updated_at)
                VALUES ($clientId, $bucket, $rx, $tx, $now)
                ON CONFLICT (client_id, bucket_start) DO UPDATE
                   SET received_bytes    = received_bytes + excluded.received_bytes,
                       transmitted_bytes = transmitted_bytes + excluded.transmitted_bytes,
                       updated_at        = excluded.updated_at
                """,
                ("$clientId", clientId),
                ("$bucket", bucket),
                ("$rx", rx),
                ("$tx", tx),
                ("$now", now));

            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, (long Rx, long Tx)> ReadPreviousCounters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId)
    {
        using var command = connection.Sql(
            "SELECT public_key, received_bytes, transmitted_bytes FROM peer_stats WHERE node_id = $nodeId",
            ("$nodeId", nodeId));

        command.Transaction = transaction;
        using var reader = command.ExecuteReader();

        var counters = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counters[reader.GetString("public_key")] = (reader.GetInt64("received_bytes"), reader.GetInt64("transmitted_bytes"));
        }

        return counters;
    }

    /// <summary>Only people's clients: a probe is a peer on every node, but it is not a person.</summary>
    private static Dictionary<string, string> ReadClientKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.Sql("SELECT id, public_key FROM clients WHERE kind = 'user'");
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();

        var clients = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            clients[reader.GetString("public_key")] = reader.GetString("id");
        }

        return clients;
    }
}
