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
                               status, applied_revision, interface_up, backend, bundle_schema_version,
                               egress_interface, mtu, public_ip, last_seen_at, last_error, revoked, enrolled_at)
            VALUES ($id, $name, $hostname, $endpointHost, $agentKey, $agentVersion,
                    $status, 0, 0, NULL, $bundleSchema, $egress, $mtu, $publicIp, NULL, NULL, 0, $enrolledAt)
            """,
            ("$id", node.Id),
            ("$name", node.Name),
            ("$hostname", node.Hostname),
            ("$endpointHost", node.EndpointHost),
            ("$agentKey", node.AgentPublicKey),
            ("$agentVersion", node.AgentVersion),
            ("$status", node.Status),
            ("$bundleSchema", node.BundleSchemaVersion),
            ("$egress", node.EgressInterface),
            ("$mtu", node.Mtu),
            ("$publicIp", node.PublicIp),
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
                   bundle_schema_version = $bundleSchema,
                   agent_version    = $agentVersion,
                   public_ip        = COALESCE($publicIp, public_ip),
                   last_seen_at     = $seenAt,
                   -- What says how long a node has been failing when no probe is watching it.
                   last_up_at       = CASE WHEN $interfaceUp = 1 THEN $seenAt ELSE last_up_at END,
                   last_error       = $lastError,
                   status           = $status
             WHERE id = $id
            """,
            ("$id", nodeId),
            ("$revision", report.AppliedRevision),
            ("$interfaceUp", report.InterfaceUp ? 1 : 0),
            ("$backend", report.Backend),
            ("$bundleSchema", BundleSchema.Normalize(report.BundleSchemaVersion)),
            ("$agentVersion", report.AgentVersion),
            // COALESCE, not a plain assignment: a node whose address lookup failed this cycle
            // reports null, and forgetting the address it had would drop it out of the failover
            // rotation over a hiccup at an echo service.
            ("$publicIp", report.PublicIp),
            ("$seenAt", report.ReportedAt.ToStorage()),
            ("$lastError", report.LastError),
            ("$status", status));

        command.ExecuteNonQuery();
    }

    public bool SetFailoverPreferences(string id, int priority, bool automatic)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE nodes SET failover_priority = $priority, auto_failover = $automatic WHERE id = $id",
            ("$id", id),
            ("$priority", priority),
            ("$automatic", automatic ? 1 : 0));

        return command.ExecuteNonQuery() > 0;
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
    /// Zeroes a peer's counters as the panel reports them, by recording what every node reads for
    /// it right now. The counters themselves live in the kernel and cannot be reset without
    /// tearing the peer down, which would drop a live tunnel, so the panel subtracts instead.
    /// </summary>
    public void ResetPeerCounters(string publicKey, DateTimeOffset at)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO peer_stat_baselines (node_id, public_key, received_bytes, transmitted_bytes, reset_at)
            SELECT node_id, public_key, received_bytes, transmitted_bytes, $at
              FROM peer_stats
             WHERE public_key = $publicKey
            ON CONFLICT (node_id, public_key) DO UPDATE
               SET received_bytes    = excluded.received_bytes,
                   transmitted_bytes = excluded.transmitted_bytes,
                   reset_at          = excluded.reset_at
            """,
            ("$publicKey", publicKey),
            ("$at", at.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>Drops everything the fleet remembers about a peer. A node stops reporting a deleted
    /// peer on its next status report, but nothing else would ever clear its baseline.</summary>
    public void ForgetPeer(string publicKey)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var table in (string[])["peer_stats", "peer_stat_baselines"])
        {
            using var delete = connection.Sql($"DELETE FROM {table} WHERE public_key = $publicKey", ("$publicKey", publicKey));
            delete.Transaction = transaction;
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Aggregates counters across the fleet: a client may be connected through any node, and with
    /// active-active it may even move between them, so the newest handshake wins and byte counters
    /// are summed.
    /// </summary>
    public IReadOnlyDictionary<string, PeerTotals> AggregatePeerStats()
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            SELECT s.node_id, s.public_key, s.latest_handshake_at, s.received_bytes, s.transmitted_bytes,
                   b.received_bytes AS base_rx, b.transmitted_bytes AS base_tx, b.reset_at
              FROM peer_stats s
              LEFT JOIN peer_stat_baselines b
                     ON b.node_id = s.node_id AND b.public_key = s.public_key
            """);
        using var reader = command.ExecuteReader();

        var stats = new Dictionary<string, PeerTotals>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var key = reader.GetString("public_key");
            var handshake = reader.GetTimestampOrNull("latest_handshake_at");
            var nodeId = reader.GetString("node_id");
            var resetAt = reader.GetTimestampOrNull("reset_at");
            var rx = SinceReset(reader.GetInt64("received_bytes"), reader.GetInt64OrNull("base_rx"));
            var tx = SinceReset(reader.GetInt64("transmitted_bytes"), reader.GetInt64OrNull("base_tx"));

            if (stats.TryGetValue(key, out var existing))
            {
                var newest = AtLeastAsNew(existing.Handshake, handshake);
                stats[key] = new PeerTotals(
                    newest ? existing.Handshake : handshake,
                    existing.Rx + rx,
                    existing.Tx + tx,
                    newest ? existing.NodeId : nodeId,
                    AtLeastAsNew(existing.ResetAt, resetAt) ? existing.ResetAt : resetAt);
            }
            else
            {
                stats[key] = new PeerTotals(handshake, rx, tx, nodeId, resetAt);
            }
        }

        return stats;
    }

    /// <summary>
    /// Null is no reading at all, which is older than any reading - never newer. Comparing two
    /// nullable timestamps with <c>&gt;=</c> answers false in both directions when either is null,
    /// so a node that reports a peer with no handshake would erase the handshake another node
    /// reported, depending only on the order the scan happened to return the rows in. Every node
    /// carries every peer, so in a fleet most nodes report most peers as never seen, and
    /// <see cref="ReplacePeerStats"/> re-inserts a node's rows at the end of the table on every
    /// report - which is what made the whole list flip to "never" and back between two refreshes.
    /// </summary>
    private static bool AtLeastAsNew(DateTimeOffset? held, DateTimeOffset? candidate)
        => candidate is null || (held is { } seen && seen >= candidate.Value);

    /// <summary>
    /// A counter below its own baseline means the device started counting again - the node
    /// rebooted, or the interface was brought down - so everything it reads now is traffic since
    /// the reset, and the baseline no longer applies.
    /// </summary>
    private static long SinceReset(long reported, long? baseline)
        => baseline is null || reported < baseline ? reported : reported - baseline.Value;

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
                reader.GetInt32("bundle_schema_version"),
                reader.GetStringOrNull("egress_interface"),
                reader.GetInt32OrNull("mtu"),
                reader.GetStringOrNull("public_ip"),
                reader.GetTimestampOrNull("last_seen_at"),
                reader.GetStringOrNull("last_error"),
                reader.GetBoolean("revoked"),
                reader.GetTimestamp("enrolled_at"),
                reader.GetTimestampOrNull("last_up_at"),
                reader.GetInt32("failover_priority"),
                reader.GetBoolean("auto_failover")));
        }

        return nodes;
    }
}

/// <summary>
/// One client's counters as the whole fleet sees them, already net of any reset.
/// </summary>
/// <param name="NodeId">The node that saw the newest handshake - where this peer is right now.</param>
/// <param name="ResetAt">When an operator last zeroed the counters, or null if never.</param>
public readonly record struct PeerTotals(
    DateTimeOffset? Handshake,
    long Rx,
    long Tx,
    string? NodeId,
    DateTimeOffset? ResetAt);

public static class NodeStatuses
{
    public const string Provisioning = "provisioning";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Down = "down";
    public const string Retired = "retired";
}
