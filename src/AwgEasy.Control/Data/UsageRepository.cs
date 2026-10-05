using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>How wide one point of a series is. The table is hourly; a day is folded out of it.</summary>
public enum UsageGrain
{
    Hour,
    Day
}

/// <summary>
/// One point of a traffic series. <paramref name="At"/> is the instant the bucket starts, as UTC.
/// For an hour that is a UTC hour; for a day it is the midnight that day began in the timezone the
/// series was asked for, which is not UTC midnight unless that is where the reader is.
/// </summary>
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
    /// <param name="offsetMinutes">
    /// The reader's offset east of UTC, which decides where one day ends and the next begins. It
    /// does nothing to an hourly series: the rows are written on UTC hour boundaries, so a zone
    /// offset by :30 or :45 has no local hour boundary that could be rebuilt out of them. The
    /// instant is right either way - only its label is local, and that is the caller's business.
    /// </param>
    public IReadOnlyList<UsageBucket> Series(
        DateTimeOffset from,
        DateTimeOffset to,
        UsageGrain grain,
        string? clientId = null,
        int offsetMinutes = 0)
    {
        // substr over an ISO-8601 string is the cheapest possible date_trunc: every bucket is
        // written in UTC with the same format, so 13 characters is an hour. A day has to be
        // truncated in the reader's own frame instead, which is a shift before the same cut.
        var bucket = grain == UsageGrain.Day
            ? "strftime('%Y-%m-%d', bucket_start, $shift)"
            : "substr(bucket_start, 1, 13)";

        using var connection = database.Open();
        using var command = connection.Sql(
            $"""
            SELECT {bucket} AS bucket,
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
            ("$clientId", clientId),
            ("$shift", Shift(offsetMinutes)));

        using var reader = command.ExecuteReader();

        var series = new List<UsageBucket>();
        while (reader.Read())
        {
            series.Add(new UsageBucket(
                ParseBucket(reader.GetString("bucket"), grain, offsetMinutes),
                reader.GetInt64("rx"),
                reader.GetInt64("tx")));
        }

        return series;
    }

    /// <summary>
    /// What each peer moved over the window. Peers with nothing in it are absent. Active days are
    /// counted in the reader's own days, for the same reason a daily series is bucketed in them.
    /// </summary>
    public IReadOnlyDictionary<string, ClientUsageTotals> ByClient(DateTimeOffset from, DateTimeOffset to, int offsetMinutes = 0)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT client_id,
                   SUM(received_bytes)                      AS rx,
                   SUM(transmitted_bytes)                   AS tx,
                   COUNT(DISTINCT strftime('%Y-%m-%d', bucket_start, $shift)) AS active_days,
                   MAX(updated_at)                          AS last_at
              FROM client_usage
             WHERE bucket_start >= $from
               AND bucket_start <  $to
             GROUP BY client_id
            """,
            ("$from", Key(from)),
            ("$to", Key(to)),
            ("$shift", Shift(offsetMinutes)));

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

    /// <summary>
    /// The offset as a SQLite modifier. Stored timestamps carry their own <c>+00:00</c>, so SQLite
    /// reads them as UTC and this shifts from there into the reader's frame.
    /// </summary>
    private static string Shift(int offsetMinutes)
        => string.Create(CultureInfo.InvariantCulture, $"{offsetMinutes:+0;-0;+0} minutes");

    /// <summary>
    /// A grouping key back into the instant its bucket starts. An hour key is already UTC; a day
    /// key is a date in the reader's frame, so the instant is the midnight that date began there.
    /// </summary>
    private static DateTimeOffset ParseBucket(string key, UsageGrain grain, int offsetMinutes)
    {
        if (grain != UsageGrain.Day)
        {
            return DateTimeOffset.Parse($"{key}:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        var date = DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
    }
}
