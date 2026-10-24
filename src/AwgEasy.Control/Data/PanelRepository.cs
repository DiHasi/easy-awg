namespace AwgEasy.Control;

/// <summary>
/// One panel of the pair as the fleet knows it. The configuration half is replicated in the
/// snapshot; everything from <see cref="Url"/> down is what that panel last reported about itself
/// and arrives on the live stream.
/// </summary>
/// <param name="Url">Where this panel is reached by the other one. Null means it cannot be
/// watched, which is the one thing the fencing depends on.</param>
/// <param name="PublicIp">What the panel record should point at for it, if a record is moved.</param>
/// <param name="Term">The term it last reported being in. The pair agrees on who is in charge by
/// comparing these, and nothing else.</param>
/// <param name="SnapshotAt">The stamp of the newest snapshot it has applied, so the panel it
/// replicates can say how far behind it is without having to ask.</param>
public sealed record PanelRecord(
    string Id,
    string Name,
    string? Hostname,
    string AgentPublicKey,
    bool Revoked,
    DateTimeOffset EnrolledAt,
    string? Url = null,
    string? PublicIp = null,
    string? AgentVersion = null,
    string Role = PanelRoles.Standby,
    long Term = 0,
    DateTimeOffset? SnapshotAt = null,
    DateTimeOffset? LastSeenAt = null,
    string? LastError = null);

/// <summary>The fleet's view of the pair: who is in charge, since when, at which term, and
/// whether a standby may take over without being asked.</summary>
/// <param name="Term">
/// Monotonic, incremented once per promotion, and the whole of the fencing. A panel that finds a
/// peer answering at a higher term knows it has been superseded - which is arithmetic, not a
/// judgement, and is why it steps down without consulting anybody.
/// </param>
public sealed record PanelPairState(
    long Term,
    string? ActivePanelId,
    DateTimeOffset? PromotedAt,
    string FailoverMode);

public sealed class PanelRepository(Database database)
{
    public PanelRecord? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM panels WHERE id = $id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public PanelRecord? FindByAgentKey(string agentPublicKey)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT * FROM panels WHERE agent_public_key = $key",
            ("$key", agentPublicKey));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<PanelRecord> List()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM panels ORDER BY enrolled_at");
        using var reader = command.ExecuteReader();

        var panels = new List<PanelRecord>();
        while (reader.Read())
        {
            panels.Add(Read(reader));
        }

        return panels;
    }

    public void Insert(PanelRecord panel)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO panels (id, name, hostname, agent_public_key, url, public_ip, agent_version,
                                role, term, revoked, enrolled_at)
            VALUES ($id, $name, $hostname, $key, $url, $publicIp, $version, $role, $term, $revoked, $enrolledAt)
            """,
            ("$id", panel.Id),
            ("$name", panel.Name),
            ("$hostname", panel.Hostname),
            ("$key", panel.AgentPublicKey),
            ("$url", panel.Url),
            ("$publicIp", panel.PublicIp),
            ("$version", panel.AgentVersion),
            ("$role", panel.Role),
            ("$term", panel.Term),
            ("$revoked", panel.Revoked ? 1 : 0),
            ("$enrolledAt", panel.EnrolledAt.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// What a peer just told us about itself. <c>last_seen_at</c> is stamped with this panel's
    /// clock rather than the peer's, for the same reason a node's status report is: whether a
    /// replica has gone quiet is judged here, and a peer whose clock runs ahead must not be able
    /// to look fresh for minutes after it stopped reporting.
    /// </summary>
    public void RecordState(string id, PanelStatusReport report, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE panels
               SET url           = COALESCE($url, url),
                   public_ip     = COALESCE($publicIp, public_ip),
                   agent_version = $version,
                   role          = $role,
                   term          = $term,
                   snapshot_at   = $snapshotAt,
                   last_seen_at  = $now,
                   last_error    = $error
             WHERE id = $id
            """,
            ("$id", id),
            // COALESCE for the same reason the node's address is kept: forgetting where a peer
            // lives over one report that omitted it would take it out of the pair.
            ("$url", report.Url),
            ("$publicIp", report.PublicIp),
            ("$version", report.Version),
            ("$role", report.Role),
            ("$term", report.Term),
            ("$snapshotAt", report.SnapshotAt.ToStorage()),
            ("$error", report.LastError),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>What this panel reports about itself, so its own row reads the same on both sides
    /// of the pair rather than only on the replica that told it.</summary>
    public void RecordSelf(string id, string role, long term, string? url, string? publicIp, DateTimeOffset? snapshotAt, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE panels
               SET url           = COALESCE($url, url),
                   public_ip     = COALESCE($publicIp, public_ip),
                   agent_version = $version,
                   role          = $role,
                   term          = $term,
                   snapshot_at   = $snapshotAt,
                   last_seen_at  = $now
             WHERE id = $id
            """,
            ("$id", id),
            ("$url", url),
            ("$publicIp", publicIp),
            ("$version", PanelVersion.Current),
            ("$role", role),
            ("$term", term),
            ("$snapshotAt", snapshotAt.ToStorage()),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public bool SetRevoked(string id, bool revoked)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE panels SET revoked = $revoked WHERE id = $id",
            ("$id", id),
            ("$revoked", revoked ? 1 : 0));

        return command.ExecuteNonQuery() == 1;
    }

    public bool Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.Sql("DELETE FROM panels WHERE id = $id", ("$id", id));
        return command.ExecuteNonQuery() == 1;
    }

    public PanelPairState FindPairState()
    {
        using var connection = database.Open();
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

    /// <summary>
    /// Takes charge at the next term, in one statement so the term and the panel that holds it can
    /// never disagree. The returned term is what the new active answers its peer with, and the
    /// only thing the peer needs in order to know it has lost.
    /// </summary>
    public long Promote(string panelId, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet
               SET panel_term        = panel_term + 1,
                   active_panel_id   = $panelId,
                   panel_promoted_at = $now,
                   updated_at        = $now
             WHERE id = 1
            RETURNING panel_term
            """,
            ("$panelId", panelId),
            ("$now", now.ToStorage()));

        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Writes down that the panel already serving the fleet is the one in charge, without moving
    /// the term past 1. Called the first time a standby is enrolled: a lone panel has no pair to
    /// fence against and therefore no term, and inventing promotions for it would make the first
    /// enrollment look like a failover in the log.
    /// </summary>
    public long AdoptAsActive(string panelId, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet
               SET panel_term        = MAX(panel_term, 1),
                   active_panel_id   = $panelId,
                   panel_promoted_at = COALESCE(panel_promoted_at, $now),
                   updated_at        = $now
             WHERE id = 1
            RETURNING panel_term
            """,
            ("$panelId", panelId),
            ("$now", now.ToStorage()));

        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Writes down that another panel has taken charge, so a panel that has just stepped down
    /// stops naming itself as the one in charge before the first snapshot from the winner arrives.
    /// The caller has already established that this term is the higher one.
    /// </summary>
    public void AcknowledgeActive(string panelId, long term, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet
               SET panel_term        = $term,
                   active_panel_id   = $panelId,
                   panel_promoted_at = $now,
                   updated_at        = $now
             WHERE id = 1
            """,
            ("$panelId", panelId),
            ("$term", term),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void SetFailoverMode(string mode, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET panel_failover_mode = $mode, updated_at = $now WHERE id = 1",
            ("$mode", mode),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    private static PanelRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(
            reader.GetString("id"),
            reader.GetString("name"),
            reader.GetStringOrNull("hostname"),
            reader.GetString("agent_public_key"),
            reader.GetBoolean("revoked"),
            reader.GetTimestamp("enrolled_at"),
            reader.GetStringOrNull("url"),
            reader.GetStringOrNull("public_ip"),
            reader.GetStringOrNull("agent_version"),
            reader.GetString("role"),
            reader.GetInt64("term"),
            reader.GetTimestampOrNull("snapshot_at"),
            reader.GetTimestampOrNull("last_seen_at"),
            reader.GetStringOrNull("last_error"));
}
