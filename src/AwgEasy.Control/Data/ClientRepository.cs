using System.Text.Json;
using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

public sealed class ClientRepository(Database database)
{
    private const string Columns = "id, name, address, private_key, public_key, preshared_key, enabled, obfuscation_json, "
        + "group_id, config_generation, egress_label, expires_at, quota_bytes, quota_window, quota_started_at, "
        + "quota_used_bytes, disabled_by, allowed_ips, dns, created_at, updated_at";

    /// <summary>The same columns for the one query that has to join the groups to sort by them.</summary>
    private const string QualifiedColumns = "c.id, c.name, c.address, c.private_key, c.public_key, c.preshared_key, "
        + "c.enabled, c.obfuscation_json, c.group_id, c.config_generation, c.egress_label, c.expires_at, "
        + "c.quota_bytes, c.quota_window, c.quota_started_at, c.quota_used_bytes, c.disabled_by, "
        + "c.allowed_ips, c.dns, c.created_at, c.updated_at";

    /// <summary>
    /// The people the fleet serves, in exactly the order the panel draws them: the groups in the
    /// operator's order, the peers inside each in theirs, and the peers filed under nobody last.
    /// Probe clients are left out: they are managed as probes. created_at breaks every tie, so a
    /// fleet nobody has dragged anything in still reads in a stable order.
    /// </summary>
    public IReadOnlyList<ClientRecord> List()
    {
        using var connection = database.Open();
        return Read(
            connection,
            $"SELECT {QualifiedColumns} FROM clients c LEFT JOIN client_groups g ON g.id = c.group_id "
            + "WHERE c.kind = 'user' "
            + "ORDER BY c.group_id IS NULL, g.sort_order, g.created_at, c.sort_order, c.created_at");
    }

    /// <summary>
    /// Only enabled clients become peers; a disabled client simply vanishes from the bundle. Every
    /// kind is a peer: a probe has to handshake with a node exactly as a person's client does.
    ///
    /// Ordered by address rather than by the operator's arrangement on purpose: this list is what
    /// a bundle is built from, and dragging a peer in the panel must not change the bytes a node
    /// is handed.
    /// </summary>
    public IReadOnlyList<ClientRecord> ListEnabled()
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients WHERE enabled = 1 ORDER BY address");
    }

    /// <summary>
    /// Every peer the panel holds: probes as well as people, and the disabled ones too.
    ///
    /// What a fleet rotation has to walk. A disabled peer's config is in somebody's hands exactly
    /// like an enabled one's, so leaving it on the old preshared key would hand that key material
    /// back the moment the peer was enabled again.
    /// </summary>
    public IReadOnlyList<ClientRecord> ListAll()
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients ORDER BY address");
    }

    /// <summary>A person's client. Probe clients are not reachable through the client endpoints.</summary>
    public ClientRecord? Find(string id)
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients WHERE id = $id AND kind = 'user'", ("$id", id)).FirstOrDefault();
    }

    public ClientRecord? FindProbeClient(string id)
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients WHERE id = $id AND kind = 'probe'", ("$id", id)).FirstOrDefault();
    }

    public bool NameExists(string name, string? excludingId = null)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT COUNT(*) FROM clients WHERE name = $name COLLATE NOCASE AND ($excluding IS NULL OR id <> $excluding)",
            ("$name", name),
            ("$excluding", excludingId));

        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// The peers a node counts as carrying somebody: people's clients, enabled.
    ///
    /// Probes are left out because a probe is a peer on every node by design and handshakes with
    /// all of them, so counting one would show every node permanently occupied - the same filter
    /// <see cref="UsageAccounting"/> makes, for the same reason. Disabled peers are left out so a
    /// node cannot claim to carry somebody the peer list draws as off: the list already refuses to
    /// call a disabled peer online, and a node disagreeing with it reads as a bug rather than as
    /// the one poll interval of lag it is.
    /// </summary>
    public IReadOnlySet<string> ServedPublicKeys()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT public_key FROM clients WHERE kind = 'user' AND enabled = 1");
        using var reader = command.ExecuteReader();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public IReadOnlySet<string> UsedAddresses()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT address FROM clients");
        using var reader = command.ExecuteReader();

        var addresses = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            addresses.Add(reader.GetString(0));
        }

        return addresses;
    }

    public void Insert(ClientRecord client, string kind = ClientKinds.User)
    {
        var limits = client.Limits ?? PeerLimits.None;
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO clients (id, name, address, private_key, public_key, preshared_key, enabled, obfuscation_json, created_at, updated_at, kind, group_id, config_generation, egress_label,
                                 expires_at, quota_bytes, quota_window, quota_started_at, allowed_ips, dns, sort_order)
            VALUES ($id, $name, $address, $privateKey, $publicKey, $presharedKey, $enabled, $obfuscation, $createdAt, $updatedAt, $kind, $groupId, $configGeneration, $egressLabel,
                    $expiresAt, $quotaBytes, $quotaWindow, $quotaStartedAt, $allowedIps, $dns,
                    (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM clients WHERE group_id IS $groupId))
            """,
            ("$kind", kind),
            ("$expiresAt", limits.ExpiresAt.ToStorage()),
            ("$quotaBytes", limits.QuotaBytes),
            ("$quotaWindow", limits.QuotaWindow),
            ("$quotaStartedAt", limits.QuotaStartedAt.ToStorage()),
            ("$allowedIps", client.AllowedIps),
            ("$dns", client.Dns),
            ("$egressLabel", client.EgressLabel),
            ("$configGeneration", client.ConfigGeneration),
            ("$groupId", client.GroupId),
            ("$id", client.Id),
            ("$name", client.Name),
            ("$address", client.Address),
            ("$privateKey", client.PrivateKey),
            ("$publicKey", client.PublicKey),
            ("$presharedKey", client.PresharedKey),
            ("$enabled", client.Enabled ? 1 : 0),
            ("$obfuscation", Serialize(client.Obfuscation)),
            ("$createdAt", client.CreatedAt.ToStorage()),
            ("$updatedAt", client.UpdatedAt.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Which address this peer leaves through, by name. Null unpins it.
    ///
    /// Unlike a rename this does change what a node runs - the panel resolves the label into an
    /// address while it builds each bundle - so the call site bumps the revision.
    /// </summary>
    public bool SetEgressLabel(string id, string? label, DateTimeOffset now)
        => Execute(
            "UPDATE clients SET egress_label = $label, updated_at = $now WHERE id = $id AND kind = 'user'",
            ("$id", id),
            ("$label", label),
            ("$now", now.ToStorage()));

    public bool Rename(string id, string name, DateTimeOffset now)
        => Execute("UPDATE clients SET name = $name, updated_at = $now WHERE id = $id AND kind = 'user'", ("$id", id), ("$name", name), ("$now", now.ToStorage()));

    /// <summary>
    /// Rewrites the listed buckets: each peer lands in that bucket's group, in the order given.
    /// Peers the caller did not list are left alone, so an arrangement sent from one browser
    /// cannot move a peer another one created in the meantime.
    ///
    /// Deliberately no revision bump at the call site: filing a peer under a person changes no
    /// peer material, so no node has anything to re-apply - the same reason a rename does not.
    /// </summary>
    /// <returns>False when a listed peer is not a person's client, having changed nothing.</returns>
    public bool Arrange(IReadOnlyList<ClientPlacement> buckets, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var bucket in buckets)
        {
            for (var position = 0; position < bucket.ClientIds.Count; position++)
            {
                using var command = connection.Sql(
                    "UPDATE clients SET group_id = $groupId, sort_order = $order, updated_at = $now WHERE id = $id AND kind = 'user'",
                    ("$groupId", bucket.GroupId),
                    ("$order", position),
                    ("$now", now.ToStorage()),
                    ("$id", bucket.ClientIds[position]));

                command.Transaction = transaction;
                if (command.ExecuteNonQuery() != 1)
                {
                    // The caller is working from a list that no longer exists. Applying the rest
                    // of it would scatter the peers it does still name.
                    transaction.Rollback();
                    return false;
                }
            }
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Records that this peer's config has been rendered at <paramref name="generation"/>, which
    /// is the only moment the panel can know somebody was in a position to be handed it.
    ///
    /// Deliberately does not touch <c>updated_at</c>: that says when the peer's own material last
    /// changed, and rendering a config changes nothing about the peer.
    /// </summary>
    public void StampConfigGeneration(string id, int generation)
        => Execute("UPDATE clients SET config_generation = $generation WHERE id = $id", ("$id", id), ("$generation", generation));

    /// <summary>
    /// How many of the people's peers are still holding a config from an older generation - the
    /// number an operator needs after a rotation, and 0 at every other time.
    ///
    /// A peer no config was ever rendered for does not count: nothing of its is in anybody's
    /// hands, so there is nobody to go and find.
    /// </summary>
    public int CountStaleConfigs(int generation)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT COUNT(*) FROM clients WHERE kind = 'user' AND config_generation <> 0 AND config_generation <> $generation",
            ("$generation", generation));

        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// What this peer routes and which resolver it names. Null on either means the fleet value.
    ///
    /// No revision bump at the call site, and this one is not even bookkeeping the panel keeps for
    /// itself: it is read when a config is rendered and nowhere else. No node is told, no bundle
    /// changes - but the file the person is holding is now out of date, which is the one thing
    /// about it the operator has to act on.
    /// </summary>
    /// <returns>False when there is no such peer.</returns>
    public bool SetRouting(string id, string? allowedIps, string? dns, DateTimeOffset now)
        => Execute(
            "UPDATE clients SET allowed_ips = $allowedIps, dns = $dns, updated_at = $now WHERE id = $id AND kind = 'user'",
            ("$id", id),
            ("$allowedIps", allowedIps),
            ("$dns", dns),
            ("$now", now.ToStorage()));

    /// <summary>
    /// What this peer is allowed. Deliberately no revision bump at the call site: a quota reaches
    /// no node and changes no peer material, exactly like a group or a rename. What a node does
    /// eventually see is the peer being disabled when a limit is reached, and that is an ordinary
    /// disable which bumps the revision like any other.
    ///
    /// Setting an allowance where there was none starts counting from now rather than from
    /// whatever the peer has moved in its life: a quota handed out today is an allowance for
    /// today onwards, and crediting it with a year of history would exhaust it before the
    /// operator had finished typing. Raising or lowering an existing one leaves the count alone,
    /// because that is a change of allowance and not a new one.
    /// </summary>
    /// <returns>False when there is no such peer.</returns>
    public bool SetLimits(string id, DateTimeOffset? expiresAt, long? quotaBytes, string quotaWindow, DateTimeOffset now)
        => Execute(
            """
            UPDATE clients
               SET expires_at       = $expiresAt,
                   quota_bytes      = $quotaBytes,
                   quota_window     = $quotaWindow,
                   quota_started_at = CASE
                                          WHEN $quotaBytes IS NULL THEN NULL
                                          WHEN quota_bytes IS NULL OR quota_started_at IS NULL THEN $now
                                          WHEN quota_window <> $quotaWindow THEN $now
                                          ELSE quota_started_at
                                      END,
                   quota_used_bytes = CASE
                                          WHEN $quotaBytes IS NULL THEN 0
                                          WHEN quota_bytes IS NULL OR quota_window <> $quotaWindow THEN 0
                                          ELSE quota_used_bytes
                                      END,
                   updated_at       = $now
             WHERE id = $id AND kind = 'user'
            """,
            ("$id", id),
            ("$expiresAt", expiresAt.ToStorage()),
            ("$quotaBytes", quotaBytes),
            ("$quotaWindow", quotaWindow),
            ("$now", now.ToStorage()));

    /// <summary>
    /// Starts the allowance again from zero. The caller re-enables the peer when the quota is what
    /// switched it off - which is the whole point of resetting one - and that is a disable undone,
    /// so it bumps the revision there.
    /// </summary>
    public bool ResetQuota(string id, DateTimeOffset now)
        => Execute(
            """
            UPDATE clients
               SET quota_used_bytes = 0,
                   quota_started_at = CASE WHEN quota_bytes IS NULL THEN NULL ELSE $now END,
                   updated_at       = $now
             WHERE id = $id AND kind = 'user'
            """,
            ("$id", id),
            ("$now", now.ToStorage()));

    /// <summary>
    /// Switches a peer off because it reached a limit, recording which one. Written together so
    /// the reason can never be left behind on a peer that is back on, which is what decides
    /// whether a monthly renewal puts it back.
    /// </summary>
    public bool Disable(string id, string reason, DateTimeOffset now)
        => Execute(
            "UPDATE clients SET enabled = 0, disabled_by = $reason, updated_at = $now WHERE id = $id AND kind = 'user' AND enabled = 1",
            ("$id", id),
            ("$reason", reason),
            ("$now", now.ToStorage()));

    /// <summary>
    /// A new calendar month: the allowance starts again, and the peers the quota itself took out
    /// come back. A peer an operator disabled by hand has no reason recorded, so it stays off -
    /// a renewal must not quietly undo somebody's decision.
    /// </summary>
    /// <returns>The peers that were switched back on, so the caller can say whose service
    /// resumed and bump the revision once for all of them.</returns>
    public IReadOnlyList<string> RenewMonthlyQuota(string id, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var reinstated = new List<string>();
        using (var read = connection.Sql(
            "SELECT disabled_by FROM clients WHERE id = $id AND kind = 'user'",
            ("$id", id)))
        {
            read.Transaction = transaction;
            if (read.ExecuteScalar() as string == PeerDisableReasons.Quota)
            {
                reinstated.Add(id);
            }
        }

        using (var update = connection.Sql(
            """
            UPDATE clients
               SET quota_used_bytes = 0,
                   quota_started_at = $monthStart,
                   enabled          = CASE WHEN disabled_by = $quota THEN 1 ELSE enabled END,
                   disabled_by      = CASE WHEN disabled_by = $quota THEN NULL ELSE disabled_by END,
                   updated_at       = $now
             WHERE id = $id AND kind = 'user'
            """,
            ("$id", id),
            ("$quota", PeerDisableReasons.Quota),
            ("$monthStart", PeerLimitPolicy.MonthStart(now).ToStorage()),
            ("$now", now.ToStorage())))
        {
            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return reinstated;
    }

    /// <summary>
    /// Every peer that has a limit of any kind, which is what the sweep walks. A fleet where
    /// nobody has one costs it a query that returns nothing.
    /// </summary>
    public IReadOnlyList<ClientRecord> ListLimited()
    {
        using var connection = database.Open();
        return Read(
            connection,
            $"SELECT {Columns} FROM clients WHERE kind = 'user' AND (expires_at IS NOT NULL OR quota_bytes IS NOT NULL) ORDER BY address");
    }

    /// <summary>
    /// An operator switching a peer on or off by hand. Clears the panel's own reason either way:
    /// a peer somebody enabled is no longer off because of a quota, and a peer somebody disabled
    /// is off because they said so, which is what keeps a monthly renewal from putting it back.
    /// </summary>
    public bool SetEnabled(string id, bool enabled, DateTimeOffset now)
        => Execute(
            "UPDATE clients SET enabled = $enabled, disabled_by = NULL, updated_at = $now WHERE id = $id",
            ("$id", id),
            ("$enabled", enabled ? 1 : 0),
            ("$now", now.ToStorage()));

    public bool Delete(string id) => Execute("DELETE FROM clients WHERE id = $id", ("$id", id));

    /// <summary>Replacing the client list on import replaces people, not the probes watching the fleet.</summary>
    public void DeleteAll()
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM clients WHERE kind = 'user'");
        command.ExecuteNonQuery();
    }

    private bool Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.Sql(sql, parameters);
        return command.ExecuteNonQuery() > 0;
    }

    private static List<ClientRecord> Read(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        using var reader = command.ExecuteReader();

        var clients = new List<ClientRecord>();
        while (reader.Read())
        {
            clients.Add(new ClientRecord(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetString("address"),
                reader.GetString("private_key"),
                reader.GetString("public_key"),
                reader.GetString("preshared_key"),
                reader.GetBoolean("enabled"),
                Deserialize(reader.GetStringOrNull("obfuscation_json")),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("updated_at"),
                reader.GetStringOrNull("group_id"),
                reader.GetInt32("config_generation"),
                reader.GetStringOrNull("egress_label"),
                ReadLimits(reader),
                reader.GetStringOrNull("allowed_ips"),
                reader.GetStringOrNull("dns")));
        }

        return clients;
    }

    /// <summary>
    /// Null rather than an empty <see cref="PeerLimits"/> for a peer nobody has limited, so
    /// "has limits" is a question about the object and not about reading six fields - and so the
    /// vast majority of peers, which have none, carry nothing.
    /// </summary>
    private static PeerLimits? ReadLimits(SqliteDataReader reader)
    {
        var expiresAt = reader.GetTimestampOrNull("expires_at");
        var quota = reader.GetInt64OrNull("quota_bytes");
        var disabledBy = reader.GetStringOrNull("disabled_by");

        if (expiresAt is null && quota is null && disabledBy is null)
        {
            return null;
        }

        return new PeerLimits(
            expiresAt,
            quota,
            reader.GetString("quota_window"),
            reader.GetTimestampOrNull("quota_started_at"),
            reader.GetInt64("quota_used_bytes"),
            disabledBy);
    }

    private static string? Serialize(ClientObfuscationOverrides? value)
        => value is null || value.IsEmpty ? null : JsonSerializer.Serialize(value, ControlJsonContext.Default.ClientObfuscationOverrides);

    private static ClientObfuscationOverrides? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, ControlJsonContext.Default.ClientObfuscationOverrides);
}

public static class ClientKinds
{
    public const string User = "user";
    public const string Probe = "probe";
}
