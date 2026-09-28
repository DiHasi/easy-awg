using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>
/// Connection factory and schema owner.
///
/// SQLite with hand-written SQL rather than an ORM: the model is small, the queries are simple,
/// and it keeps the panel's dependency surface close to the "no database server, just files you
/// can back up and inspect" spirit of the original.
/// </summary>
public sealed class Database(ControlOptions options, ILogger<Database> logger)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL keeps readers (agent polls, stats) from blocking writers (client changes).
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        // CREATE TABLE IF NOT EXISTS does nothing to a table that already exists, so a column
        // added after a panel was first deployed has to be filled in here. Defaulting to 1 is
        // deliberate: a node that has not yet reported its bundle schema is assumed to be the
        // older one, so it is never handed settings its AmneziaWG build would reject.
        EnsureColumn(connection, "nodes", "bundle_schema_version", "INTEGER NOT NULL DEFAULT 1");

        // Both added with DNS failover. A node fills its address in on the next status report, and
        // no fleet has an active node until an operator picks one, so NULL is the honest start.
        EnsureColumn(connection, "nodes", "public_ip", "TEXT NULL");
        EnsureColumn(connection, "fleet", "active_node_id", "TEXT NULL");
        EnsureColumn(connection, "fleet", "active_node_set_at", "TEXT NULL");

        if (!OperatingSystem.IsWindows() && File.Exists(options.DatabasePath))
        {
            // The database holds the fleet private key: never group- or world-readable.
            File.SetUnixFileMode(options.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogInformation("Control plane database ready at {Path}.", options.DatabasePath);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (columns.ExecuteScalar() is not null)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS fleet (
            id                   INTEGER PRIMARY KEY CHECK (id = 1),
            generation           INTEGER NOT NULL,
            server_private_key   TEXT    NOT NULL,
            server_public_key    TEXT    NOT NULL,
            signing_private_key  TEXT    NOT NULL,
            signing_public_key   TEXT    NOT NULL,
            signing_key_id       TEXT    NOT NULL,
            subnet               TEXT    NOT NULL,
            listen_port          INTEGER NOT NULL,
            client_allowed_ips   TEXT    NOT NULL,
            client_dns           TEXT    NULL,
            endpoint_host        TEXT    NOT NULL,
            obfuscation_json     TEXT    NULL,
            revision             INTEGER NOT NULL,
            active_node_id       TEXT    NULL,
            active_node_set_at   TEXT    NULL,
            created_at           TEXT    NOT NULL,
            updated_at           TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS clients (
            id             TEXT PRIMARY KEY,
            name           TEXT    NOT NULL COLLATE NOCASE,
            address        TEXT    NOT NULL,
            private_key    TEXT    NOT NULL,
            public_key     TEXT    NOT NULL,
            preshared_key  TEXT    NOT NULL,
            enabled        INTEGER NOT NULL DEFAULT 1,
            obfuscation_json TEXT  NULL,
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_name ON clients (name COLLATE NOCASE);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_address ON clients (address);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_public_key ON clients (public_key);

        CREATE TABLE IF NOT EXISTS nodes (
            id                TEXT PRIMARY KEY,
            name              TEXT    NOT NULL,
            hostname          TEXT    NULL,
            endpoint_host     TEXT    NULL,
            agent_public_key  TEXT    NOT NULL,
            agent_version     TEXT    NULL,
            status            TEXT    NOT NULL,
            applied_revision  INTEGER NOT NULL DEFAULT 0,
            interface_up      INTEGER NOT NULL DEFAULT 0,
            backend           TEXT    NULL,
            bundle_schema_version INTEGER NOT NULL DEFAULT 1,
            egress_interface  TEXT    NULL,
            mtu               INTEGER NULL,
            public_ip         TEXT    NULL,
            last_seen_at      TEXT    NULL,
            last_error        TEXT    NULL,
            revoked           INTEGER NOT NULL DEFAULT 0,
            enrolled_at       TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nodes_agent_key ON nodes (agent_public_key);

        CREATE TABLE IF NOT EXISTS enrollment_tokens (
            token_hash      TEXT PRIMARY KEY,
            node_name       TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            expires_at      TEXT NOT NULL,
            used_at         TEXT NULL,
            used_by_node_id TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS client_shares (
            token_hash TEXT PRIMARY KEY,
            client_id  TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_client_shares_client ON client_shares (client_id);

        CREATE TABLE IF NOT EXISTS admins (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            username      TEXT NOT NULL COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            created_at    TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_admins_username ON admins (username COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS peer_stats (
            node_id             TEXT    NOT NULL,
            public_key          TEXT    NOT NULL,
            latest_handshake_at TEXT    NULL,
            received_bytes      INTEGER NOT NULL,
            transmitted_bytes   INTEGER NOT NULL,
            reported_at         TEXT    NOT NULL,
            PRIMARY KEY (node_id, public_key)
        );

        -- What a peer's counters read when an operator last zeroed them in the panel. The kernel
        -- has no way to reset a peer counter, so "reset" is a subtraction: the totals reported
        -- here are what the device reports minus this. Kept per node because the nodes count
        -- independently and restart independently.
        CREATE TABLE IF NOT EXISTS peer_stat_baselines (
            node_id           TEXT    NOT NULL,
            public_key        TEXT    NOT NULL,
            received_bytes    INTEGER NOT NULL,
            transmitted_bytes INTEGER NOT NULL,
            reset_at          TEXT    NOT NULL,
            PRIMARY KEY (node_id, public_key)
        );

        CREATE TABLE IF NOT EXISTS events (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            at       TEXT NOT NULL,
            kind     TEXT NOT NULL,
            actor    TEXT NULL,
            node_id  TEXT NULL,
            message  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_at ON events (at DESC);
        """;
}
