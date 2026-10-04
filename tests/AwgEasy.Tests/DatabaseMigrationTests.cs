using System.Globalization;
using AwgEasy.Control;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwgEasy.Tests;

/// <summary>
/// Opening a database that predates a column. The peer list is the first thing an operator looks
/// at after an upgrade, so the order it comes up in is part of the migration, not a detail.
/// </summary>
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "awg-tests", Guid.NewGuid().ToString("N") + ".db");

    public DatabaseMigrationTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    /// <summary>The clients table as a panel without grouping wrote it.</summary>
    private void WriteUngroupedClients(params (string Id, string Address)[] clients)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE clients (
                    id             TEXT PRIMARY KEY,
                    name           TEXT    NOT NULL COLLATE NOCASE,
                    address        TEXT    NOT NULL,
                    private_key    TEXT    NOT NULL,
                    public_key     TEXT    NOT NULL,
                    preshared_key  TEXT    NOT NULL,
                    enabled        INTEGER NOT NULL DEFAULT 1,
                    obfuscation_json TEXT  NULL,
                    kind           TEXT    NOT NULL DEFAULT 'user',
                    created_at     TEXT    NOT NULL,
                    updated_at     TEXT    NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        // Created in an order that is not the address order, which is the whole point: the list
        // has always been drawn by address, and an upgrade must not resort it by creation time.
        var created = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (id, address) in clients)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO clients (id, name, address, private_key, public_key, preshared_key, created_at, updated_at)
                VALUES ($id, $id, $address, 'priv', $id, 'psk', $created, $created)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$address", address);
            insert.Parameters.AddWithValue("$created", created.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
            created = created.AddMinutes(1);
        }
    }

    [Fact]
    public void An_upgraded_panel_opens_on_the_peer_order_it_already_showed()
    {
        WriteUngroupedClients(("second", "10.8.0.12"), ("third", "10.8.0.100"), ("first", "10.8.0.9"));

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var clients = new ClientRepository(database).List();

        // Dotted quads, numerically: .9 before .12 before .100, not the string order and not the
        // order the rows were written in.
        Assert.Equal(["first", "second", "third"], clients.Select(client => client.Name));
        Assert.All(clients, client => Assert.Null(client.GroupId));
    }

    private ControlOptions Options() => new(
        DatabasePath: _path,
        DefaultSubnet: "10.8.0.0/24",
        DefaultListenPort: 51820,
        DefaultClientAllowedIps: "0.0.0.0/0, ::/0",
        DefaultClientDns: null,
        DefaultEndpointHost: "vpn.example.com",
        BundleLifetime: TimeSpan.FromMinutes(15),
        BootstrapAdminUser: null,
        BootstrapAdminPassword: null,
        LegacyStateImportPath: null,
        Dns: new DnsFailoverOptions(RecordName: null, Ttl: 60, CloudflareApiToken: null, CloudflareZoneId: null),
        Failover: new FailoverOptions(
            CheckInterval: TimeSpan.FromHours(1),
            Grace: TimeSpan.Zero,
            Cooldown: TimeSpan.Zero,
            AgentStaleAfter: TimeSpan.FromMinutes(5),
            ProbeStaleAfter: TimeSpan.FromMinutes(5),
            ProbeInterval: TimeSpan.FromSeconds(60),
            ProbeHandshakeTimeout: TimeSpan.FromSeconds(15),
            ProbeCheckUrls: FailoverOptions.DefaultProbeCheckUrls,
            ProbeTrafficTimeout: TimeSpan.FromSeconds(8)),
        Notifications: new NotificationOptions(null, null, null));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A lingering handle is not worth failing a test run over.
            }
        }
    }
}
