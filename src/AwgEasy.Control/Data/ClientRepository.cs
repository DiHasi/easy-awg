using System.Text.Json;
using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

public sealed class ClientRepository(Database database)
{
    private const string Columns = "id, name, address, private_key, public_key, preshared_key, enabled, obfuscation_json, created_at, updated_at";

    /// <summary>The people the fleet serves. Probe clients are left out: they are managed as probes.</summary>
    public IReadOnlyList<ClientRecord> List()
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients WHERE kind = 'user' ORDER BY created_at");
    }

    /// <summary>
    /// Only enabled clients become peers; a disabled client simply vanishes from the bundle. Every
    /// kind is a peer: a probe has to handshake with a node exactly as a person's client does.
    /// </summary>
    public IReadOnlyList<ClientRecord> ListEnabled()
    {
        using var connection = database.Open();
        return Read(connection, $"SELECT {Columns} FROM clients WHERE enabled = 1 ORDER BY address");
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
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO clients (id, name, address, private_key, public_key, preshared_key, enabled, obfuscation_json, created_at, updated_at, kind)
            VALUES ($id, $name, $address, $privateKey, $publicKey, $presharedKey, $enabled, $obfuscation, $createdAt, $updatedAt, $kind)
            """,
            ("$kind", kind),
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

    public bool Rename(string id, string name, DateTimeOffset now)
        => Execute("UPDATE clients SET name = $name, updated_at = $now WHERE id = $id AND kind = 'user'", ("$id", id), ("$name", name), ("$now", now.ToStorage()));

    public bool SetEnabled(string id, bool enabled, DateTimeOffset now)
        => Execute("UPDATE clients SET enabled = $enabled, updated_at = $now WHERE id = $id", ("$id", id), ("$enabled", enabled ? 1 : 0), ("$now", now.ToStorage()));

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
                reader.GetTimestamp("updated_at")));
        }

        return clients;
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
