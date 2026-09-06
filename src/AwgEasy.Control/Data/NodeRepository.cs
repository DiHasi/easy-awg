using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

public sealed class NodeRepository(Database database)
{
    public IReadOnlyList<NodeRecord> List()
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM nodes ORDER BY enrolled_at");
    }

    public NodeRecord? Find(string id)
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM nodes WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    /// <summary>An agent keeps one key pair for its lifetime, so this identifies a returning server.</summary>
    public NodeRecord? FindByAgentKey(string agentPublicKey)
    {
        using var connection = database.Open();
        return Read(connection, "SELECT * FROM nodes WHERE agent_public_key = $key", ("$key", agentPublicKey)).FirstOrDefault();
    }

    public void Insert(NodeRecord node)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO nodes (id, name, hostname, endpoint_host, agent_public_key, agent_version,
                               status, applied_revision, interface_up, backend, egress_interface, mtu,
                               last_seen_at, last_error, revoked, enrolled_at)
            VALUES ($id, $name, $hostname, $endpointHost, $agentKey, $agentVersion,
                    $status, 0, 0, NULL, $egress, $mtu, NULL, NULL, 0, $enrolledAt)
            """,
            ("$id", node.Id),
            ("$name", node.Name),
            ("$hostname", node.Hostname),
            ("$endpointHost", node.EndpointHost),
            ("$agentKey", node.AgentPublicKey),
            ("$agentVersion", node.AgentVersion),
            ("$status", node.Status),
            ("$egress", node.EgressInterface),
            ("$mtu", node.Mtu),
            ("$enrolledAt", node.EnrolledAt.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void RecordStatus(string nodeId, NodeStatusReport report, string status)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE nodes
               SET applied_revision = $revision,
                   interface_up     = $interfaceUp,
                   backend          = $backend,
                   agent_version    = $agentVersion,
                   last_seen_at     = $seenAt,
                   last_error       = $lastError,
                   status           = $status
             WHERE id = $id
            """,
            ("$id", nodeId),
            ("$revision", report.AppliedRevision),
            ("$interfaceUp", report.InterfaceUp ? 1 : 0),
            ("$backend", report.Backend),
            ("$agentVersion", report.AgentVersion),
            ("$seenAt", report.ReportedAt.ToStorage()),
            ("$lastError", report.LastError),
            ("$status", status));

        command.ExecuteNonQuery();
    }

    public bool SetRevoked(string id, bool revoked)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE nodes SET revoked = $revoked, status = $status WHERE id = $id",
            ("$id", id),
            ("$revoked", revoked ? 1 : 0),
            ("$status", revoked ? NodeStatuses.Retired : NodeStatuses.Provisioning));

        return command.ExecuteNonQuery() > 0;
    }

    public bool Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM nodes WHERE id = $id", ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    public void ReplacePeerStats(string nodeId, PeerStatus[] peers, DateTimeOffset reportedAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.Sql("DELETE FROM peer_stats WHERE node_id = $nodeId", ("$nodeId", nodeId)))
        {
            delete.Transaction = transaction;
            delete.ExecuteNonQuery();
        }

        foreach (var peer in peers)
        {
            using var insert = connection.Sql(
                """
                INSERT INTO peer_stats (node_id, public_key, latest_handshake_at, received_bytes, transmitted_bytes, reported_at)
                VALUES ($nodeId, $publicKey, $handshake, $rx, $tx, $reportedAt)
                """,
                ("$nodeId", nodeId),
                ("$publicKey", peer.PublicKey),
                ("$handshake", peer.LatestHandshakeAt.ToStorage()),
                ("$rx", peer.ReceivedBytes),
                ("$tx", peer.TransmittedBytes),
                ("$reportedAt", reportedAt.ToStorage()));

            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Aggregates counters across the fleet: a client may be connected through any node, and with
    /// active-active it may even move between them, so the newest handshake wins and byte counters
    /// are summed.
    /// </summary>
    public IReadOnlyDictionary<string, (DateTimeOffset? Handshake, long Rx, long Tx, string? NodeId)> AggregatePeerStats()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT node_id, public_key, latest_handshake_at, received_bytes, transmitted_bytes FROM peer_stats");
        using var reader = command.ExecuteReader();

        var stats = new Dictionary<string, (DateTimeOffset?, long, long, string?)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var key = reader.GetString("public_key");
            var handshake = reader.GetTimestampOrNull("latest_handshake_at");
            var rx = reader.GetInt64("received_bytes");
            var tx = reader.GetInt64("transmitted_bytes");
            var nodeId = reader.GetString("node_id");

            if (stats.TryGetValue(key, out var existing))
            {
                var newest = existing.Item1 >= handshake ? existing.Item1 : handshake;
                var newestNode = existing.Item1 >= handshake ? existing.Item4 : nodeId;
                stats[key] = (newest, existing.Item2 + rx, existing.Item3 + tx, newestNode);
            }
            else
            {
                stats[key] = (handshake, rx, tx, nodeId);
            }
        }

        return stats;
    }

    private static List<NodeRecord> Read(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        using var reader = command.ExecuteReader();

        var nodes = new List<NodeRecord>();
        while (reader.Read())
        {
            nodes.Add(new NodeRecord(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetStringOrNull("hostname"),
                reader.GetStringOrNull("endpoint_host"),
                reader.GetString("agent_public_key"),
                reader.GetStringOrNull("agent_version"),
                reader.GetString("status"),
                reader.GetInt64("applied_revision"),
                reader.GetBoolean("interface_up"),
                reader.GetStringOrNull("backend"),
                reader.GetStringOrNull("egress_interface"),
                reader.GetInt32OrNull("mtu"),
                reader.GetTimestampOrNull("last_seen_at"),
                reader.GetStringOrNull("last_error"),
                reader.GetBoolean("revoked"),
                reader.GetTimestamp("enrolled_at")));
        }

        return nodes;
    }
}

public static class NodeStatuses
{
    public const string Provisioning = "provisioning";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Down = "down";
    public const string Retired = "retired";
}
