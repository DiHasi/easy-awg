using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>
/// Reads the replicated set out of the active panel's database and writes it into a standby's.
///
/// The split between the two payloads is the one distinction this whole feature rests on, and it
/// is the same one the rest of the panel already draws: the snapshot is <em>what the fleet runs
/// on</em> - the identity, the peers, the nodes and the arrangement, everything an operator
/// decided - while the stream is <em>what the panel observed</em>: the reports, the counters and
/// the histories. They are apart because they change on completely different clocks. The snapshot
/// changes when somebody does something, so it is pulled whole and gated by an ETag over its own
/// bytes; the stream changes every twenty seconds, so it is pulled every poll, with the latest-
/// state tables replaced wholesale and the histories read from a cursor.
///
/// Rows are carried as rows. The obfuscation profile travels as the JSON the column holds rather
/// than as a reparsed object, so what a standby stores is byte-for-byte what the active stores:
/// a fleet's wire format must not be able to drift because two panels serialized it differently.
///
/// What is deliberately <em>not</em> replicated is the event log. It is each panel's own account
/// of what was done on it, its key is a local autoincrement that two writers cannot share without
/// renumbering one of them, and a panel that takes over has its own story to tell from the moment
/// it took over - which the promotion itself writes into it.
/// </summary>
public sealed class PanelSnapshotRepository(Database database)
{
    // ------------------------------------------------------------------ build: the configuration

    /// <summary>
    /// Builds the snapshot with its stamps left at default, which is what the ETag is taken over.
    /// The caller stamps and signs the copy it actually sends; hashing a stamped payload would
    /// make every poll a change, and every poll a full transfer of the fleet.
    /// </summary>
    public PanelSnapshot BuildSnapshot(string panelId)
    {
        using var connection = database.Open();
        var pair = ReadPairState(connection);

        return new PanelSnapshot(
            PanelSchema.CurrentVersion,
            panelId,
            pair.Term,
            pair.ActivePanelId,
            default,
            default,
            ReadFleet(connection),
            [..Query(connection, "SELECT * FROM panels ORDER BY enrolled_at", reader => new PanelPeerRow(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetStringOrNull("hostname"),
                reader.GetString("agent_public_key"),
                reader.GetBoolean("revoked"),
                reader.GetTimestamp("enrolled_at")))],
            [..Query(connection, "SELECT * FROM clients ORDER BY id", reader => new PanelClientRow(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetString("address"),
                reader.GetString("private_key"),
                reader.GetString("public_key"),
                reader.GetString("preshared_key"),
                reader.GetBoolean("enabled"),
                reader.GetStringOrNull("obfuscation_json"),
                reader.GetString("kind"),
                reader.GetStringOrNull("group_id"),
                reader.GetInt32("sort_order"),
                reader.GetInt32("config_generation"),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("updated_at")))],
            [..Query(connection, "SELECT * FROM client_groups ORDER BY id", reader => new PanelGroupRow(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetInt32("sort_order"),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("updated_at")))],
            [..Query(connection, "SELECT * FROM nodes ORDER BY enrolled_at", reader => new PanelNodeConfigRow(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetStringOrNull("hostname"),
                reader.GetStringOrNull("endpoint_host"),
                reader.GetString("agent_public_key"),
                reader.GetStringOrNull("egress_interface"),
                reader.GetInt32OrNull("mtu"),
                reader.GetBoolean("revoked"),
                reader.GetInt32("failover_priority"),
                reader.GetBoolean("auto_failover"),
                reader.GetTimestamp("enrolled_at")))],
            [..Query(connection, "SELECT * FROM probes ORDER BY enrolled_at", reader => new PanelProbeConfigRow(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetStringOrNull("hostname"),
                reader.GetString("agent_public_key"),
                reader.GetString("client_id"),
                reader.GetBoolean("revoked"),
                reader.GetTimestamp("enrolled_at")))],
            [..Query(connection, "SELECT * FROM admins ORDER BY id", reader => new PanelAdminRow(
                reader.GetString("username"),
                reader.GetString("password_hash"),
                reader.GetTimestamp("created_at")))],
            [..Query(connection, "SELECT * FROM enrollment_tokens ORDER BY created_at", reader => new PanelTokenRow(
                reader.GetString("token_hash"),
                reader.GetString("node_name"),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("expires_at"),
                reader.GetTimestampOrNull("used_at"),
                reader.GetStringOrNull("used_by_node_id"),
                reader.GetString("kind"),
                reader.GetStringOrNull("endpoint_host"),
                reader.GetStringOrNull("egress_interface"),
                reader.GetInt32OrNull("mtu")))],
            [..Query(connection, "SELECT * FROM client_shares ORDER BY created_at", reader => new PanelShareRow(
                reader.GetString("token_hash"),
                reader.GetString("client_id"),
                reader.GetInt32("generation"),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("expires_at")))],
            [..Query(connection, "SELECT * FROM peer_stat_baselines", reader => new PanelBaselineRow(
                reader.GetString("node_id"),
                reader.GetString("public_key"),
                reader.GetInt64("received_bytes"),
                reader.GetInt64("transmitted_bytes"),
                reader.GetTimestamp("reset_at")))]);
    }

    // ------------------------------------------------------------------------ build: the stream

    /// <param name="cursors">Where this standby's history left off, one per table.</param>
    public PanelStream BuildStream(string panelId, PanelStreamCursors cursors, int batchSize)
    {
        using var connection = database.Open();
        var pair = ReadPairState(connection);

        return new PanelStream(
            PanelSchema.CurrentVersion,
            panelId,
            pair.Term,
            pair.ActivePanelId,
            default,
            default,
            [..Query(connection, "SELECT * FROM nodes", reader => new PanelNodeStateRow(
                reader.GetString("id"),
                reader.GetStringOrNull("agent_version"),
                reader.GetString("status"),
                reader.GetInt64("applied_revision"),
                reader.GetBoolean("interface_up"),
                reader.GetStringOrNull("backend"),
                reader.GetInt32("bundle_schema_version"),
                reader.GetStringOrNull("public_ip"),
                reader.GetTimestampOrNull("last_seen_at"),
                reader.GetStringOrNull("last_error"),
                reader.GetTimestampOrNull("last_up_at")))],
            [..Query(connection, "SELECT id, agent_version, last_seen_at FROM probes", reader => new PanelProbeStateRow(
                reader.GetString("id"),
                reader.GetStringOrNull("agent_version"),
                reader.GetTimestampOrNull("last_seen_at")))],
            [..Query(connection, "SELECT * FROM panels", reader => new PanelPeerStateRow(
                reader.GetString("id"),
                reader.GetStringOrNull("url"),
                reader.GetStringOrNull("public_ip"),
                reader.GetStringOrNull("agent_version"),
                reader.GetString("role"),
                reader.GetInt64("term"),
                reader.GetTimestampOrNull("snapshot_at"),
                reader.GetTimestampOrNull("last_seen_at"),
                reader.GetStringOrNull("last_error")))],
            [..Query(connection, "SELECT * FROM peer_stats", reader => new PanelPeerStatRow(
                reader.GetString("node_id"),
                reader.GetString("public_key"),
                reader.GetTimestampOrNull("latest_handshake_at"),
                reader.GetInt64("received_bytes"),
                reader.GetInt64("transmitted_bytes"),
                reader.GetTimestamp("reported_at")))],
            [..Query(connection, "SELECT * FROM probe_results", reader => new PanelProbeResultRow(
                reader.GetString("probe_id"),
                reader.GetString("node_id"),
                reader.GetString("address"),
                reader.GetString("outcome"),
                reader.GetTimestamp("checked_at"),
                reader.GetTimestampOrNull("last_reachable_at"),
                reader.GetTimestampOrNull("failing_since"),
                reader.GetInt32OrNull("handshake") is { } handshake ? handshake != 0 : null,
                reader.GetInt32OrNull("latency_ms"),
                reader.GetStringOrNull("detail")))],
            ReadHistory(connection, "client_usage", cursors.Usage, batchSize, reader => new PanelUsageRow(
                    reader.GetString("client_id"),
                    reader.GetTimestamp("bucket_start"),
                    reader.GetInt64("received_bytes"),
                    reader.GetInt64("transmitted_bytes"),
                    reader.GetTimestamp("updated_at")),
                (rows, cursor, more) => new PanelUsageBatch(rows, cursor, more)),
            ReadHistory(connection, "node_availability", cursors.NodeAvailability, batchSize, reader => new PanelNodeAvailabilityRow(
                    reader.GetString("node_id"),
                    reader.GetTimestamp("bucket_start"),
                    reader.GetInt32("samples"),
                    reader.GetInt32("healthy"),
                    reader.GetInt32("blocked"),
                    reader.GetInt32("down"),
                    reader.GetInt32("silent"),
                    reader.GetInt32("unknown"),
                    reader.GetTimestamp("updated_at")),
                (rows, cursor, more) => new PanelNodeAvailabilityBatch(rows, cursor, more)),
            ReadHistory(connection, "probe_availability", cursors.ProbeAvailability, batchSize, reader => new PanelProbeAvailabilityRow(
                    reader.GetString("probe_id"),
                    reader.GetString("node_id"),
                    reader.GetTimestamp("bucket_start"),
                    reader.GetInt32("reachable"),
                    reader.GetInt32("unreachable"),
                    reader.GetInt32("errors"),
                    reader.GetInt32("handshakes"),
                    reader.GetTimestamp("updated_at")),
                (rows, cursor, more) => new PanelProbeAvailabilityBatch(rows, cursor, more)));
    }

    /// <summary>
    /// One batch of an append-mostly history, read strictly after the cursor and cut on a whole
    /// timestamp.
    ///
    /// The cut is the subtlety. <c>updated_at</c> is not unique - one accrual writes a row per
    /// peer with the same stamp - so a batch that stopped in the middle of such a group would
    /// either lose the rest of it, if the cursor advanced, or never get past it, if it did not.
    /// The boundary subquery finds the stamp of the row that would have been last and takes every
    /// row up to and including it, so a batch always ends on a timestamp boundary and the cursor
    /// can advance strictly past it.
    /// </summary>
    private static TBatch ReadHistory<TRow, TBatch>(
        SqliteConnection connection,
        string table,
        string? cursor,
        int batchSize,
        Func<SqliteDataReader, TRow> read,
        Func<TRow[], string?, bool, TBatch> batch)
    {
        var from = cursor ?? string.Empty;

        string? boundary;
        using (var probe = connection.Sql(
            $"SELECT updated_at FROM {table} WHERE updated_at > $from ORDER BY updated_at LIMIT 1 OFFSET $skip",
            ("$from", from),
            ("$skip", batchSize - 1)))
        {
            boundary = probe.ExecuteScalar() as string;
        }

        var rows = Query(
            connection,
            $"""
             SELECT * FROM {table}
              WHERE updated_at > $from
                AND ($boundary IS NULL OR updated_at <= $boundary)
              ORDER BY updated_at
             """,
            read,
            ("$from", from),
            ("$boundary", boundary)).ToArray();

        // The boundary is only set when at least batchSize rows were waiting, so it doubles as
        // the "there is more" answer. Without one, this batch reached the end of the table.
        var next = boundary ?? LastStamp(connection, table, from) ?? cursor;
        return batch(rows, next, boundary is not null);
    }

    /// <summary>The newest stamp at or after the cursor, so a batch that emptied the table leaves
    /// the cursor at the end rather than re-reading the last rows forever.</summary>
    private static string? LastStamp(SqliteConnection connection, string table, string from)
    {
        using var command = connection.Sql(
            $"SELECT MAX(updated_at) FROM {table} WHERE updated_at > $from",
            ("$from", from));

        return command.ExecuteScalar() as string;
    }

    // ------------------------------------------------------------------------ apply: the standby

    /// <summary>
    /// Replaces this panel's configuration with the active's, in one transaction.
    ///
    /// One transaction for the same reason a rotation is one: a half-applied snapshot is a panel
    /// holding one fleet's identity and another fleet's peers, which serves nobody and cannot say
    /// why. Nothing here is merged - the active is the source of truth for every one of these
    /// tables, and a standby that kept a row of its own would be a second place a fleet is
    /// decided.
    ///
    /// The node and probe rows are the exception, and only in one direction: their configuration
    /// is overwritten while the columns the agent reports are left alone, because those arrive on
    /// the stream and a snapshot that blanked them would make every node read as never-seen
    /// between polls.
    /// </summary>
    public void ApplySnapshot(PanelSnapshot snapshot)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var fleet = snapshot.Fleet;
        Execute(
            connection, transaction,
            """
            INSERT INTO fleet (id, generation, server_private_key, server_public_key,
                               signing_private_key, signing_public_key, signing_key_id,
                               subnet, listen_port, client_allowed_ips, client_dns, tunnel_mtu,
                               endpoint_host, obfuscation_json, revision, active_node_id,
                               active_node_set_at, failover_mode, identity_revision,
                               identity_rotated_at, panel_term, active_panel_id,
                               panel_promoted_at, panel_failover_mode, created_at, updated_at)
            VALUES (1, $generation, $serverPrivate, $serverPublic,
                    $signingPrivate, $signingPublic, $signingKeyId,
                    $subnet, $listenPort, $allowedIps, $dns, $tunnelMtu,
                    $endpointHost, $obfuscation, $revision, $activeNodeId,
                    $activeNodeSetAt, $failoverMode, $identityRevision,
                    $identityRotatedAt, $panelTerm, $activePanelId,
                    $panelPromotedAt, $panelFailoverMode, $createdAt, $updatedAt)
            ON CONFLICT (id) DO UPDATE SET
                generation = excluded.generation,
                server_private_key = excluded.server_private_key,
                server_public_key = excluded.server_public_key,
                signing_private_key = excluded.signing_private_key,
                signing_public_key = excluded.signing_public_key,
                signing_key_id = excluded.signing_key_id,
                subnet = excluded.subnet,
                listen_port = excluded.listen_port,
                client_allowed_ips = excluded.client_allowed_ips,
                client_dns = excluded.client_dns,
                tunnel_mtu = excluded.tunnel_mtu,
                endpoint_host = excluded.endpoint_host,
                obfuscation_json = excluded.obfuscation_json,
                revision = excluded.revision,
                active_node_id = excluded.active_node_id,
                active_node_set_at = excluded.active_node_set_at,
                failover_mode = excluded.failover_mode,
                identity_revision = excluded.identity_revision,
                identity_rotated_at = excluded.identity_rotated_at,
                panel_term = excluded.panel_term,
                active_panel_id = excluded.active_panel_id,
                panel_promoted_at = excluded.panel_promoted_at,
                panel_failover_mode = excluded.panel_failover_mode,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at
            """,
            ("$generation", fleet.Generation),
            ("$serverPrivate", fleet.ServerPrivateKey),
            ("$serverPublic", fleet.ServerPublicKey),
            ("$signingPrivate", fleet.SigningPrivateKey),
            ("$signingPublic", fleet.SigningPublicKey),
            ("$signingKeyId", fleet.SigningKeyId),
            ("$subnet", fleet.Subnet),
            ("$listenPort", fleet.ListenPort),
            ("$allowedIps", fleet.ClientAllowedIps),
            ("$dns", fleet.ClientDns),
            ("$tunnelMtu", fleet.TunnelMtu),
            ("$endpointHost", fleet.EndpointHost),
            ("$obfuscation", fleet.ObfuscationJson),
            ("$revision", fleet.Revision),
            ("$activeNodeId", fleet.ActiveNodeId),
            ("$activeNodeSetAt", fleet.ActiveNodeSetAt.ToStorage()),
            ("$failoverMode", fleet.FailoverMode),
            ("$identityRevision", fleet.IdentityRevision),
            ("$identityRotatedAt", fleet.IdentityRotatedAt.ToStorage()),
            ("$panelTerm", fleet.PanelTerm),
            ("$activePanelId", fleet.ActivePanelId),
            ("$panelPromotedAt", fleet.PanelPromotedAt.ToStorage()),
            ("$panelFailoverMode", fleet.PanelFailoverMode),
            ("$createdAt", fleet.CreatedAt.ToStorage()),
            ("$updatedAt", fleet.UpdatedAt.ToStorage()));

        ReplaceAll(connection, transaction, "clients", snapshot.Clients,
            """
            INSERT INTO clients (id, name, address, private_key, public_key, preshared_key, enabled,
                                 obfuscation_json, kind, group_id, sort_order, config_generation,
                                 created_at, updated_at)
            VALUES ($id, $name, $address, $private, $public, $psk, $enabled,
                    $obfuscation, $kind, $groupId, $sortOrder, $generation, $createdAt, $updatedAt)
            """,
            row =>
            [
                ("$id", row.Id), ("$name", row.Name), ("$address", row.Address),
                ("$private", row.PrivateKey), ("$public", row.PublicKey), ("$psk", row.PresharedKey),
                ("$enabled", row.Enabled ? 1 : 0), ("$obfuscation", row.ObfuscationJson),
                ("$kind", row.Kind), ("$groupId", row.GroupId), ("$sortOrder", row.SortOrder),
                ("$generation", row.ConfigGeneration),
                ("$createdAt", row.CreatedAt.ToStorage()), ("$updatedAt", row.UpdatedAt.ToStorage())
            ]);

        ReplaceAll(connection, transaction, "client_groups", snapshot.Groups,
            """
            INSERT INTO client_groups (id, name, sort_order, created_at, updated_at)
            VALUES ($id, $name, $sortOrder, $createdAt, $updatedAt)
            """,
            row =>
            [
                ("$id", row.Id), ("$name", row.Name), ("$sortOrder", row.SortOrder),
                ("$createdAt", row.CreatedAt.ToStorage()), ("$updatedAt", row.UpdatedAt.ToStorage())
            ]);

        // The one table that is not replaced when the snapshot is empty. A panel in charge with
        // no admin account is a misconfiguration - it warns and keeps running - and copying that
        // emptiness here would lock the operator out of the only panel they could still reach, in
        // exactly the situation a standby exists for.
        if (snapshot.Admins.Length > 0)
        {
            ReplaceAll(connection, transaction, "admins", snapshot.Admins,
                "INSERT INTO admins (username, password_hash, created_at) VALUES ($username, $hash, $createdAt)",
                row => [("$username", row.Username), ("$hash", row.PasswordHash), ("$createdAt", row.CreatedAt.ToStorage())]);
        }

        ReplaceAll(connection, transaction, "enrollment_tokens", snapshot.Tokens,
            """
            INSERT INTO enrollment_tokens (token_hash, node_name, created_at, expires_at, used_at,
                                           used_by_node_id, kind, endpoint_host, egress_interface, mtu)
            VALUES ($hash, $name, $createdAt, $expiresAt, $usedAt, $usedBy, $kind, $endpointHost, $egress, $mtu)
            """,
            row =>
            [
                ("$hash", row.TokenHash), ("$name", row.NodeName),
                ("$createdAt", row.CreatedAt.ToStorage()), ("$expiresAt", row.ExpiresAt.ToStorage()),
                ("$usedAt", row.UsedAt.ToStorage()), ("$usedBy", row.UsedByNodeId), ("$kind", row.Kind),
                ("$endpointHost", row.EndpointHost), ("$egress", row.EgressInterface), ("$mtu", row.Mtu)
            ]);

        ReplaceAll(connection, transaction, "client_shares", snapshot.Shares,
            """
            INSERT INTO client_shares (token_hash, client_id, generation, created_at, expires_at)
            VALUES ($hash, $clientId, $generation, $createdAt, $expiresAt)
            """,
            row =>
            [
                ("$hash", row.TokenHash), ("$clientId", row.ClientId), ("$generation", row.Generation),
                ("$createdAt", row.CreatedAt.ToStorage()), ("$expiresAt", row.ExpiresAt.ToStorage())
            ]);

        ReplaceAll(connection, transaction, "peer_stat_baselines", snapshot.Baselines,
            """
            INSERT INTO peer_stat_baselines (node_id, public_key, received_bytes, transmitted_bytes, reset_at)
            VALUES ($nodeId, $key, $received, $transmitted, $resetAt)
            """,
            row =>
            [
                ("$nodeId", row.NodeId), ("$key", row.PublicKey),
                ("$received", row.ReceivedBytes), ("$transmitted", row.TransmittedBytes),
                ("$resetAt", row.ResetAt.ToStorage())
            ]);

        // Upserted rather than replaced, and the observed columns are left out of the update: a
        // node's status and applied revision arrive on the stream, and blanking them here would
        // make the whole fleet read as never-seen for the rest of the poll.
        Prune(connection, transaction, "nodes", "id", snapshot.Nodes.Select(node => node.Id));
        foreach (var node in snapshot.Nodes)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO nodes (id, name, hostname, endpoint_host, agent_public_key, status,
                                   egress_interface, mtu, revoked, failover_priority, auto_failover, enrolled_at)
                VALUES ($id, $name, $hostname, $endpointHost, $key, 'provisioning',
                        $egress, $mtu, $revoked, $priority, $auto, $enrolledAt)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    hostname = excluded.hostname,
                    endpoint_host = excluded.endpoint_host,
                    agent_public_key = excluded.agent_public_key,
                    egress_interface = excluded.egress_interface,
                    mtu = excluded.mtu,
                    revoked = excluded.revoked,
                    failover_priority = excluded.failover_priority,
                    auto_failover = excluded.auto_failover,
                    enrolled_at = excluded.enrolled_at
                """,
                ("$id", node.Id), ("$name", node.Name), ("$hostname", node.Hostname),
                ("$endpointHost", node.EndpointHost), ("$key", node.AgentPublicKey),
                ("$egress", node.EgressInterface), ("$mtu", node.Mtu),
                ("$revoked", node.Revoked ? 1 : 0), ("$priority", node.FailoverPriority),
                ("$auto", node.AutoFailover ? 1 : 0), ("$enrolledAt", node.EnrolledAt.ToStorage()));
        }

        Prune(connection, transaction, "probes", "id", snapshot.Probes.Select(probe => probe.Id));
        foreach (var probe in snapshot.Probes)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO probes (id, name, hostname, agent_public_key, client_id, revoked, enrolled_at)
                VALUES ($id, $name, $hostname, $key, $clientId, $revoked, $enrolledAt)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    hostname = excluded.hostname,
                    agent_public_key = excluded.agent_public_key,
                    client_id = excluded.client_id,
                    revoked = excluded.revoked,
                    enrolled_at = excluded.enrolled_at
                """,
                ("$id", probe.Id), ("$name", probe.Name), ("$hostname", probe.Hostname),
                ("$key", probe.AgentPublicKey), ("$clientId", probe.ClientId),
                ("$revoked", probe.Revoked ? 1 : 0), ("$enrolledAt", probe.EnrolledAt.ToStorage()));
        }

        Prune(connection, transaction, "panels", "id", snapshot.Panels.Select(panel => panel.Id));
        foreach (var panel in snapshot.Panels)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO panels (id, name, hostname, agent_public_key, revoked, enrolled_at)
                VALUES ($id, $name, $hostname, $key, $revoked, $enrolledAt)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    hostname = excluded.hostname,
                    agent_public_key = excluded.agent_public_key,
                    revoked = excluded.revoked,
                    enrolled_at = excluded.enrolled_at
                """,
                ("$id", panel.Id), ("$name", panel.Name), ("$hostname", panel.Hostname),
                ("$key", panel.AgentPublicKey), ("$revoked", panel.Revoked ? 1 : 0),
                ("$enrolledAt", panel.EnrolledAt.ToStorage()));
        }

        transaction.Commit();
    }

    /// <summary>
    /// Applies the live half: the latest-state tables wholesale, the history rows upserted.
    ///
    /// Upserted rather than inserted because a bucket is written again every time traffic is
    /// credited to it, so the same hour arrives repeatedly while it is still the current one. The
    /// cursor only ever moves forward, so a bucket that has stopped changing is never fetched
    /// again.
    /// </summary>
    public void ApplyStream(PanelStream stream)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var node in stream.Nodes)
        {
            Execute(
                connection, transaction,
                """
                UPDATE nodes
                   SET agent_version = $version, status = $status, applied_revision = $revision,
                       interface_up = $up, backend = $backend, bundle_schema_version = $schema,
                       public_ip = $publicIp, last_seen_at = $lastSeen, last_error = $error,
                       last_up_at = $lastUp
                 WHERE id = $id
                """,
                ("$id", node.Id), ("$version", node.AgentVersion), ("$status", node.Status),
                ("$revision", node.AppliedRevision), ("$up", node.InterfaceUp ? 1 : 0),
                ("$backend", node.Backend), ("$schema", node.BundleSchemaVersion),
                ("$publicIp", node.PublicIp), ("$lastSeen", node.LastSeenAt.ToStorage()),
                ("$error", node.LastError), ("$lastUp", node.LastUpAt.ToStorage()));
        }

        foreach (var probe in stream.Probes)
        {
            Execute(
                connection, transaction,
                "UPDATE probes SET agent_version = $version, last_seen_at = $lastSeen WHERE id = $id",
                ("$id", probe.Id), ("$version", probe.AgentVersion), ("$lastSeen", probe.LastSeenAt.ToStorage()));
        }

        foreach (var panel in stream.Panels)
        {
            Execute(
                connection, transaction,
                """
                UPDATE panels
                   SET url = COALESCE($url, url), public_ip = COALESCE($publicIp, public_ip),
                       agent_version = $version, role = $role, term = $term,
                       snapshot_at = $snapshotAt, last_seen_at = $lastSeen, last_error = $error
                 WHERE id = $id
                """,
                ("$id", panel.Id), ("$url", panel.Url), ("$publicIp", panel.PublicIp),
                ("$version", panel.AgentVersion), ("$role", panel.Role), ("$term", panel.Term),
                ("$snapshotAt", panel.SnapshotAt.ToStorage()),
                ("$lastSeen", panel.LastSeenAt.ToStorage()), ("$error", panel.LastError));
        }

        ReplaceAll(connection, transaction, "peer_stats", stream.PeerStats,
            """
            INSERT INTO peer_stats (node_id, public_key, latest_handshake_at, received_bytes,
                                    transmitted_bytes, reported_at)
            VALUES ($nodeId, $key, $handshake, $received, $transmitted, $reportedAt)
            """,
            row =>
            [
                ("$nodeId", row.NodeId), ("$key", row.PublicKey),
                ("$handshake", row.LatestHandshakeAt.ToStorage()),
                ("$received", row.ReceivedBytes), ("$transmitted", row.TransmittedBytes),
                ("$reportedAt", row.ReportedAt.ToStorage())
            ]);

        ReplaceAll(connection, transaction, "probe_results", stream.ProbeResults,
            """
            INSERT INTO probe_results (probe_id, node_id, address, outcome, checked_at,
                                       last_reachable_at, failing_since, handshake, latency_ms, detail)
            VALUES ($probeId, $nodeId, $address, $outcome, $checkedAt,
                    $lastReachable, $failingSince, $handshake, $latency, $detail)
            """,
            row =>
            [
                ("$probeId", row.ProbeId), ("$nodeId", row.NodeId), ("$address", row.Address),
                ("$outcome", row.Outcome), ("$checkedAt", row.CheckedAt.ToStorage()),
                ("$lastReachable", row.LastReachableAt.ToStorage()),
                ("$failingSince", row.FailingSince.ToStorage()),
                ("$handshake", row.Handshake is null ? null : row.Handshake.Value ? 1 : 0),
                ("$latency", row.LatencyMs), ("$detail", row.Detail)
            ]);

        foreach (var row in stream.Usage.Rows)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO client_usage (client_id, bucket_start, received_bytes, transmitted_bytes, updated_at)
                VALUES ($clientId, $bucket, $received, $transmitted, $updatedAt)
                ON CONFLICT (client_id, bucket_start) DO UPDATE SET
                    received_bytes = excluded.received_bytes,
                    transmitted_bytes = excluded.transmitted_bytes,
                    updated_at = excluded.updated_at
                """,
                ("$clientId", row.ClientId), ("$bucket", row.BucketStart.ToStorage()),
                ("$received", row.ReceivedBytes), ("$transmitted", row.TransmittedBytes),
                ("$updatedAt", row.UpdatedAt.ToStorage()));
        }

        foreach (var row in stream.NodeAvailability.Rows)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO node_availability (node_id, bucket_start, samples, healthy, blocked, down,
                                               silent, unknown, updated_at)
                VALUES ($nodeId, $bucket, $samples, $healthy, $blocked, $down, $silent, $unknown, $updatedAt)
                ON CONFLICT (node_id, bucket_start) DO UPDATE SET
                    samples = excluded.samples, healthy = excluded.healthy, blocked = excluded.blocked,
                    down = excluded.down, silent = excluded.silent, unknown = excluded.unknown,
                    updated_at = excluded.updated_at
                """,
                ("$nodeId", row.NodeId), ("$bucket", row.BucketStart.ToStorage()),
                ("$samples", row.Samples), ("$healthy", row.Healthy), ("$blocked", row.Blocked),
                ("$down", row.Down), ("$silent", row.Silent), ("$unknown", row.Unknown),
                ("$updatedAt", row.UpdatedAt.ToStorage()));
        }

        foreach (var row in stream.ProbeAvailability.Rows)
        {
            Execute(
                connection, transaction,
                """
                INSERT INTO probe_availability (probe_id, node_id, bucket_start, reachable, unreachable,
                                                errors, handshakes, updated_at)
                VALUES ($probeId, $nodeId, $bucket, $reachable, $unreachable, $errors, $handshakes, $updatedAt)
                ON CONFLICT (probe_id, node_id, bucket_start) DO UPDATE SET
                    reachable = excluded.reachable, unreachable = excluded.unreachable,
                    errors = excluded.errors, handshakes = excluded.handshakes,
                    updated_at = excluded.updated_at
                """,
                ("$probeId", row.ProbeId), ("$nodeId", row.NodeId), ("$bucket", row.BucketStart.ToStorage()),
                ("$reachable", row.Reachable), ("$unreachable", row.Unreachable),
                ("$errors", row.Errors), ("$handshakes", row.Handshakes),
                ("$updatedAt", row.UpdatedAt.ToStorage()));
        }

        transaction.Commit();
    }

    // ------------------------------------------------------------------------------------ helpers

    private static PanelPairState ReadPairState(SqliteConnection connection)
    {
        using var command = connection.Sql(
            "SELECT panel_term, active_panel_id, panel_promoted_at, panel_failover_mode FROM fleet WHERE id = 1");

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PanelPairState(
                reader.GetInt64("panel_term"),
                reader.GetStringOrNull("active_panel_id"),
                reader.GetTimestampOrNull("panel_promoted_at"),
                reader.GetString("panel_failover_mode"))
            : new PanelPairState(0, null, null, PanelFailoverModes.Manual);
    }

    private static PanelFleetState ReadFleet(SqliteConnection connection)
    {
        using var command = connection.Sql("SELECT * FROM fleet WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Fleet has not been initialized.");
        }

        return new PanelFleetState(
            reader.GetInt32("generation"),
            reader.GetString("server_private_key"),
            reader.GetString("server_public_key"),
            reader.GetString("signing_private_key"),
            reader.GetString("signing_public_key"),
            reader.GetString("signing_key_id"),
            reader.GetString("subnet"),
            reader.GetInt32("listen_port"),
            reader.GetString("client_allowed_ips"),
            reader.GetStringOrNull("client_dns"),
            reader.GetInt32("tunnel_mtu"),
            reader.GetString("endpoint_host"),
            reader.GetStringOrNull("obfuscation_json"),
            reader.GetInt64("revision"),
            reader.GetInt64("identity_revision"),
            reader.GetTimestampOrNull("identity_rotated_at"),
            reader.GetStringOrNull("active_node_id"),
            reader.GetTimestampOrNull("active_node_set_at"),
            reader.GetString("failover_mode"),
            reader.GetInt64("panel_term"),
            reader.GetStringOrNull("active_panel_id"),
            reader.GetTimestampOrNull("panel_promoted_at"),
            reader.GetString("panel_failover_mode"),
            reader.GetTimestamp("created_at"),
            reader.GetTimestamp("updated_at"));
    }

    private static IEnumerable<T> Query<T>(
        SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static void ReplaceAll<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<T> rows,
        string insert,
        Func<T, (string Name, object? Value)[]> bind)
    {
        Execute(connection, transaction, $"DELETE FROM {table}");
        foreach (var row in rows)
        {
            Execute(connection, transaction, insert, bind(row));
        }
    }

    /// <summary>Deletes what the active no longer has. A client, node or probe removed there has
    /// to be removed here too, or a promoted panel would hand the fleet back a deleted peer.</summary>
    private static void Prune(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string keyColumn,
        IEnumerable<string> keep)
    {
        var kept = keep.ToArray();
        if (kept.Length == 0)
        {
            Execute(connection, transaction, $"DELETE FROM {table}");
            return;
        }

        var names = kept.Select((_, index) => $"$k{index}").ToArray();
        var parameters = kept.Select((value, index) => ($"$k{index}", (object?)value)).ToArray();
        Execute(connection, transaction, $"DELETE FROM {table} WHERE {keyColumn} NOT IN ({string.Join(", ", names)})", parameters);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}

/// <summary>Where a standby's history left off, one cursor per append-mostly table.</summary>
public sealed record PanelStreamCursors(string? Usage, string? NodeAvailability, string? ProbeAvailability)
{
    public static readonly PanelStreamCursors Beginning = new(null, null, null);

    public static PanelStreamCursors From(PanelLocalState state)
        => new(state.UsageCursor, state.NodeAvailabilityCursor, state.ProbeAvailabilityCursor);
}
