using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

public sealed class ProbeRepository(Database database)
{
    public IReadOnlyList<ProbeRecord> List()
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM probes ORDER BY enrolled_at");
    }

    public ProbeRecord? Find(string id)
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM probes WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    public ProbeRecord? FindByAgentKey(string agentPublicKey)
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM probes WHERE agent_public_key = $key", ("$key", agentPublicKey)).FirstOrDefault();
    }

    public void Insert(ProbeRecord probe)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO probes (id, name, hostname, agent_public_key, agent_version, client_id, last_seen_at, revoked, enrolled_at)
            VALUES ($id, $name, $hostname, $agentKey, $agentVersion, $clientId, NULL, 0, $enrolledAt)
            """,
            ("$id", probe.Id),
            ("$name", probe.Name),
            ("$hostname", probe.Hostname),
            ("$agentKey", probe.AgentPublicKey),
            ("$agentVersion", probe.AgentVersion),
            ("$clientId", probe.ClientId),
            ("$enrolledAt", probe.EnrolledAt.ToStorage()));

        command.ExecuteNonQuery();
    }

    public bool SetRevoked(string id, bool revoked)
    {
        using var connection = database.Open();
        using var command = connection.Sql("UPDATE probes SET revoked = $revoked WHERE id = $id", ("$id", id), ("$revoked", revoked ? 1 : 0));
        return command.ExecuteNonQuery() > 0;
    }

    public bool Delete(string id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var results = connection.Sql("DELETE FROM probe_results WHERE probe_id = $id", ("$id", id)))
        {
            results.Transaction = transaction;
            results.ExecuteNonQuery();
        }

        int deleted;
        using (var probe = connection.Sql("DELETE FROM probes WHERE id = $id", ("$id", id)))
        {
            probe.Transaction = transaction;
            deleted = probe.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted > 0;
    }

    /// <summary>A node that leaves the fleet takes what probes said about it along.</summary>
    public void ForgetNode(string nodeId)
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM probe_results WHERE node_id = $nodeId", ("$nodeId", nodeId));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records one round. Results about nodes the fleet does not know are dropped rather than
    /// stored: a probe reports what it was assigned, so anything else is stale or made up.
    /// </summary>
    public void RecordRound(string probeId, string? agentVersion, IEnumerable<ProbeResult> results, DateTimeOffset seenAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var seen = connection.Sql(
            "UPDATE probes SET last_seen_at = $seenAt, agent_version = COALESCE($version, agent_version) WHERE id = $id",
            ("$id", probeId),
            ("$seenAt", seenAt.ToStorage()),
            ("$version", agentVersion)))
        {
            seen.Transaction = transaction;
            seen.ExecuteNonQuery();
        }

        foreach (var result in results)
        {
            // Both timestamps carry over from the row being replaced. last_reachable_at moves only
            // when this round reached the node; failing_since is set by the first round that did
            // not, and cleared by the next one that did. An error round touches neither: it is
            // about the probe, not the node. A changed address starts both again, because what
            // was true of the old address says nothing about the new one.
            using var upsert = connection.Sql(
                """
                INSERT INTO probe_results (probe_id, node_id, address, outcome, checked_at, last_reachable_at, failing_since, handshake, latency_ms, detail)
                SELECT $probeId, $nodeId, $address, $outcome, $checkedAt,
                       CASE WHEN $outcome = 'reachable' THEN $checkedAt END,
                       CASE WHEN $outcome = 'unreachable' THEN $checkedAt END,
                       $handshake, $latency, $detail
                 WHERE EXISTS (SELECT 1 FROM nodes WHERE id = $nodeId)
                ON CONFLICT (probe_id, node_id) DO UPDATE
                   SET last_reachable_at = CASE
                           WHEN excluded.outcome = 'reachable' THEN excluded.checked_at
                           WHEN probe_results.address <> excluded.address THEN NULL
                           ELSE probe_results.last_reachable_at
                       END,
                       failing_since = CASE
                           WHEN excluded.outcome = 'reachable' THEN NULL
                           WHEN probe_results.address <> excluded.address THEN excluded.failing_since
                           WHEN excluded.outcome = 'unreachable' THEN COALESCE(probe_results.failing_since, excluded.checked_at)
                           ELSE probe_results.failing_since
                       END,
                       address    = excluded.address,
                       outcome    = excluded.outcome,
                       checked_at = excluded.checked_at,
                       handshake  = excluded.handshake,
                       latency_ms = excluded.latency_ms,
                       detail     = excluded.detail
                """,
                ("$probeId", probeId),
                ("$nodeId", result.NodeId),
                ("$address", result.Address),
                ("$outcome", result.Outcome),
                ("$checkedAt", result.CheckedAt.ToStorage()),
                ("$handshake", result.Handshake is { } handshake ? (handshake ? 1 : 0) : null),
                ("$latency", result.LatencyMs),
                ("$detail", result.Detail));

            upsert.Transaction = transaction;
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// What every probe last said. A revoked probe is no longer evidence of anything, so it is
    /// left out unless asked for - showing an operator what it said last is fine, deciding on it
    /// is not. Staleness is the caller's call.
    /// </summary>
    public IReadOnlyList<ProbeObservation> Observations(bool includeRevoked = false)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT r.*
              FROM probe_results r
              JOIN probes p ON p.id = r.probe_id
             WHERE p.revoked = 0 OR $includeRevoked = 1
            """,
            ("$includeRevoked", includeRevoked ? 1 : 0));
        using var reader = command.ExecuteReader();

        var observations = new List<ProbeObservation>();
        while (reader.Read())
        {
            observations.Add(new ProbeObservation(
                reader.GetString("probe_id"),
                reader.GetString("node_id"),
                reader.GetString("address"),
                reader.GetString("outcome"),
                reader.GetTimestamp("checked_at"),
                reader.GetTimestampOrNull("last_reachable_at"),
                reader.GetTimestampOrNull("failing_since"),
                reader.GetInt32OrNull("latency_ms"),
                reader.GetStringOrNull("detail"),
                reader.GetInt64OrNull("handshake") is { } handshake ? handshake != 0 : null));
        }

        return observations;
    }

    private static List<ProbeRecord> Read(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        using var reader = command.ExecuteReader();

        var probes = new List<ProbeRecord>();
        while (reader.Read())
        {
            probes.Add(new ProbeRecord(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetStringOrNull("hostname"),
                reader.GetString("agent_public_key"),
                reader.GetStringOrNull("agent_version"),
                reader.GetString("client_id"),
                reader.GetTimestampOrNull("last_seen_at"),
                reader.GetBoolean("revoked"),
                reader.GetTimestamp("enrolled_at")));
        }

        return probes;
    }
}
