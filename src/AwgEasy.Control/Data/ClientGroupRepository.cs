using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>
/// The operator's filing of peers: one group per person, the peers in it their devices.
///
/// Entirely panel-side. A group carries no key material, no node is ever told about it and no
/// client config changes when a peer moves between groups, which is why none of this touches the
/// fleet revision. It lives in the database rather than in a browser so that every device the
/// operator signs in from reads the same arrangement.
/// </summary>
public sealed class ClientGroupRepository(Database database)
{
    private const string Columns = "id, name, sort_order, created_at, updated_at";

    /// <summary>In the order the operator arranged them; created_at breaks the tie.</summary>
    public IReadOnlyList<ClientGroupRecord> List()
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM client_groups ORDER BY sort_order, created_at");
    }

    public ClientGroupRecord? Find(string id)
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM client_groups WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    public bool NameExists(string name, string? excludingId = null)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT COUNT(*) FROM client_groups WHERE name = $name COLLATE NOCASE AND ($excluding IS NULL OR id <> $excluding)",
            ("$name", name),
            ("$excluding", excludingId));

        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Added at the end of the list, where the operator just asked for it.</summary>
    public ClientGroupRecord Insert(string id, string name, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO client_groups (id, name, sort_order, created_at, updated_at)
            VALUES ($id, $name, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM client_groups), $now, $now)
            RETURNING sort_order
            """,
            ("$id", id),
            ("$name", name),
            ("$now", now.ToStorage()));

        var sortOrder = Convert.ToInt32(command.ExecuteScalar());
        return new ClientGroupRecord(id, name, sortOrder, now, now);
    }

    public bool Rename(string id, string name, DateTimeOffset now)
        => Execute("UPDATE client_groups SET name = $name, updated_at = $now WHERE id = $id", ("$id", id), ("$name", name), ("$now", now.ToStorage()));

    /// <summary>
    /// Deleting a group hands its peers back to the ungrouped list; it never deletes a config.
    /// A group is a label, and losing a label must not disconnect anybody - so both statements
    /// run in one transaction rather than leaving rows pointing at a group that is gone.
    /// </summary>
    public bool Delete(string id, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var release = connection.Sql(
            "UPDATE clients SET group_id = NULL, updated_at = $now WHERE group_id = $id",
            ("$id", id),
            ("$now", now.ToStorage())))
        {
            release.Transaction = transaction;
            release.ExecuteNonQuery();
        }

        using var delete = connection.Sql("DELETE FROM client_groups WHERE id = $id", ("$id", id));
        delete.Transaction = transaction;
        var deleted = delete.ExecuteNonQuery() > 0;

        if (!deleted)
        {
            transaction.Rollback();
            return false;
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Writes the order the listed groups should be read in. Groups left out keep their position,
    /// so an order sent from one browser cannot move a group another one has just created.
    /// </summary>
    /// <returns>False when one of the ids is not a group, having changed nothing.</returns>
    public bool Reorder(IReadOnlyList<string> ids, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        for (var position = 0; position < ids.Count; position++)
        {
            using var command = connection.Sql(
                "UPDATE client_groups SET sort_order = $order, updated_at = $now WHERE id = $id",
                ("$order", position),
                ("$now", now.ToStorage()),
                ("$id", ids[position]));

            command.Transaction = transaction;
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        return true;
    }

    private bool Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.Sql(sql, parameters);
        return command.ExecuteNonQuery() > 0;
    }

    private static List<ClientGroupRecord> Read(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        using var reader = command.ExecuteReader();

        var groups = new List<ClientGroupRecord>();
        while (reader.Read())
        {
            groups.Add(new ClientGroupRecord(
                reader.GetString("id"),
                reader.GetString("name"),
                reader.GetInt32("sort_order"),
                reader.GetTimestamp("created_at"),
                reader.GetTimestamp("updated_at")));
        }

        return groups;
    }
}
