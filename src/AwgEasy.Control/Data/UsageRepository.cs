using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>How wide one point of a series is. The table is hourly; a day is folded out of it.</summary>
public enum UsageGrain
{
    Hour,
    Day
}

/// <summary>One point of a traffic series. <paramref name="At"/> is the start of the bucket, UTC.</summary>
public readonly record struct UsageBucket(DateTimeOffset At, long Rx, long Tx);

/// <param name="ActiveDays">Days inside the window on which this peer moved anything at all.</param>
/// <param name="LastActiveAt">The last report that credited this peer with traffic in the window.</param>
public readonly record struct ClientUsageTotals(
    string ClientId,
    long Rx,
    long Tx,
    int ActiveDays,
    DateTimeOffset? LastActiveAt);

/// <summary>
/// Reads the traffic history back, and keeps it from growing without end.
///
/// The write side lives in <see cref="UsageAccounting"/>, in the transaction that overwrites the
/// reading it is a difference from. Everything here aggregates in SQLite rather than in memory:
/// a 90-day window over a fleet of a hundred peers is a six-figure row count, and the answer the
/// panel draws is a few hundred numbers.
/// </summary>
public sealed class UsageRepository(Database database)
{
    /// <summary>
    /// Traffic per bucket, for one client or for the whole fleet. Buckets with nothing in them are
    /// simply absent; filling the gaps is the caller's business, because only it knows the window
    /// it asked about.
    /// </summary>
    public IReadOnlyList<UsageBucket> Series(DateTimeOffset from, DateTimeOffset to, UsageGrain grain, string? clientId = null)
    {
        // substr over an ISO-8601 string is the cheapest possible date_trunc: every bucket is
        // written in UTC with the same format, so 13 characters is an hour and 10 is a day.
        var width = grain == UsageGrain.Day ? 10 : 13;

        using var connection = database.Open();
        using var command = connection.Sql(
            $"""
            SELECT substr(bucket_start, 1, {width}) AS bucket,
                   SUM(received_bytes)    AS rx,
                   SUM(transmitted_bytes) AS tx
              FROM client_usage
             WHERE bucket_start >= $from
               AND bucket_start <  $to
               AND ($clientId IS NULL OR client_id = $clientId)
             GROUP BY bucket
             ORDER BY bucket
            """,
            ("$from", Key(from)),
            ("$to", Key(to)),
            ("$clientId", clientId));

        using var reader = command.ExecuteReader();

        var series = new List<UsageBucket>();
        while (reader.Read())
        {
            series.Add(new UsageBucket(
                ParseBucket(reader.GetString("bucket"), grain),
                reader.GetInt64("rx"),
                reader.GetInt64("tx")));
        }

        return series;
    }

    /// <summary>What each peer moved over the window. Peers with nothing in it are absent.</summary>
    public IReadOnlyDictionary<string, ClientUsageTotals> ByClient(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT client_id,
                   SUM(received_bytes)                      AS rx,
                   SUM(transmitted_bytes)                   AS tx,
                   COUNT(DISTINCT substr(bucket_start, 1, 10)) AS active_days,
                   MAX(updated_at)                          AS last_at
              FROM client_usage
             WHERE bucket_start >= $from
               AND bucket_start <  $to
             GROUP BY client_id
            """,
            ("$from", Key(from)),
            ("$to", Key(to)));

        using var reader = command.ExecuteReader();

        var totals = new Dictionary<string, ClientUsageTotals>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString("client_id");
            totals[id] = new ClientUsageTotals(
                id,
                reader.GetInt64("rx"),
                reader.GetInt64("tx"),
                reader.GetInt32("active_days"),
                reader.GetTimestampOrNull("last_at"));
        }

        return totals;
    }

    /// <summary>
    /// The oldest bucket still kept, so the panel can say how far back its own numbers go rather
    /// than drawing an empty month as if nothing had happened in it.
    /// </summary>
    public DateTimeOffset? FirstRecordedAt()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT MIN(bucket_start) FROM client_usage");
        var value = command.ExecuteScalar();

        return value is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    /// <summary>
    /// Drops a person's history. Called when their last config is deleted: a group is a label and
    /// loses nothing, but deleting a peer deletes the person's traffic with it. Keeping an hourly
    /// record of someone no longer in the panel is a liability and answers nothing.
    /// </summary>
    public void Forget(string clientId)
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM client_usage WHERE client_id = $clientId", ("$clientId", clientId));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Enforces retention. Also sweeps rows whose client is gone, which catches the paths that
    /// delete clients wholesale - a legacy import that replaces the client list, most of all.
    /// </summary>
    /// <returns>How many rows were dropped.</returns>
    public int Prune(DateTimeOffset before)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using var expired = connection.Sql("DELETE FROM client_usage WHERE bucket_start < $before", ("$before", Key(before)));
        expired.Transaction = transaction;
        var dropped = expired.ExecuteNonQuery();

        using var orphans = connection.Sql("DELETE FROM client_usage WHERE client_id NOT IN (SELECT id FROM clients)");
        orphans.Transaction = transaction;
        dropped += orphans.ExecuteNonQuery();

        transaction.Commit();
        return dropped;
    }

    /// <summary>Everything is compared as the string it is stored as, so both ends must be UTC.</summary>
    private static string Key(DateTimeOffset at) => at.ToUniversalTime().ToStorage();

    private static DateTimeOffset ParseBucket(string key, UsageGrain grain)
        => DateTimeOffset.Parse(
            grain == UsageGrain.Day ? $"{key}T00:00:00Z" : $"{key}:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
