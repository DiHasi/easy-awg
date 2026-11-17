using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>One panel assessment or one result from a probe's round.</summary>
public sealed record AvailabilityCheck(
    string Id,
    string NodeId,
    string? ProbeId,
    DateTimeOffset CheckedAt,
    string Outcome,
    string? Source,
    string? Address,
    bool? Handshake,
    int? LatencyMs,
    string? Detail,
    DateTimeOffset UpdatedAt);

public sealed class AvailabilityCheckRepository(Database database)
{
    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, AvailabilityCheck check)
    {
        using var command = connection.Sql(
            """
            INSERT INTO availability_checks (id, node_id, probe_id, checked_at, outcome, source,
                                             address, handshake, latency_ms, detail, updated_at)
            VALUES ($id, $nodeId, $probeId, $checkedAt, $outcome, $source,
                    $address, $handshake, $latency, $detail, $updatedAt)
            ON CONFLICT (id) DO NOTHING
            """,
            ("$id", check.Id),
            ("$nodeId", check.NodeId),
            ("$probeId", check.ProbeId),
            ("$checkedAt", HistoryWindows.Key(check.CheckedAt)),
            ("$outcome", check.Outcome),
            ("$source", check.Source),
            ("$address", check.Address),
            ("$handshake", check.Handshake),
            ("$latency", check.LatencyMs),
            ("$detail", check.Detail),
            ("$updatedAt", HistoryWindows.Key(check.UpdatedAt)));

        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    internal static AvailabilityCheck Read(SqliteDataReader reader)
        => new(
            reader.GetString("id"),
            reader.GetString("node_id"),
            reader.GetStringOrNull("probe_id"),
            reader.GetTimestamp("checked_at"),
            reader.GetString("outcome"),
            reader.GetStringOrNull("source"),
            reader.GetStringOrNull("address"),
            reader.GetInt32OrNull("handshake") is { } handshake ? handshake != 0 : null,
            reader.GetInt32OrNull("latency_ms"),
            reader.GetStringOrNull("detail"),
            reader.GetTimestamp("updated_at"));

    public IReadOnlyList<(AvailabilityCheck Check, string NodeName, string? ProbeName)> List(
        DateTimeOffset from,
        DateTimeOffset until,
        string? beforeId,
        string? nodeId,
        string? kind,
        bool issuesOnly,
        int limit)
    {
        using var connection = database.Open();
        // Keep the node predicate indexable when a node is selected.
        var nodeFilter = nodeId is null ? "" : "AND c.node_id = $nodeId";
        using var command = connection.Sql(
            $"""
            SELECT c.*, n.name AS node_name, p.name AS probe_name
              FROM availability_checks c
              JOIN nodes n ON n.id = c.node_id
              LEFT JOIN probes p ON p.id = c.probe_id
             WHERE c.checked_at >= $from
               AND (c.checked_at, c.id) < ($until, $beforeId)
               {nodeFilter}
               AND ($kind IS NULL OR ($kind = 'panel' AND c.probe_id IS NULL)
                                  OR ($kind = 'probe' AND c.probe_id IS NOT NULL))
               AND (NOT $issuesOnly OR c.outcome NOT IN ('healthy', 'reachable'))
             ORDER BY c.checked_at DESC, c.id DESC
             LIMIT $limit
            """,
            ("$from", HistoryWindows.Key(from)),
            ("$until", HistoryWindows.Key(until)),
            ("$beforeId", beforeId ?? ""),
            ("$nodeId", nodeId),
            ("$kind", kind),
            ("$issuesOnly", issuesOnly),
            ("$limit", limit));

        using var reader = command.ExecuteReader();
        var rows = new List<(AvailabilityCheck, string, string?)>();
        while (reader.Read())
        {
            rows.Add((Read(reader), reader.GetString("node_name"), reader.GetStringOrNull("probe_name")));
        }

        return rows;
    }
}
