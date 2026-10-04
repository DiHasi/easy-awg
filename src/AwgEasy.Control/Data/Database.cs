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

        // Automatic failover. Off until an operator arms it: a panel that starts moving DNS on its
        // own after an upgrade would be a surprise nobody asked for.
        EnsureColumn(connection, "fleet", "failover_mode", "TEXT NOT NULL DEFAULT 'manual'");
        EnsureColumn(connection, "nodes", "last_up_at", "TEXT NULL");
        EnsureColumn(connection, "nodes", "failover_priority", "INTEGER NOT NULL DEFAULT 100");
        EnsureColumn(connection, "nodes", "auto_failover", "INTEGER NOT NULL DEFAULT 1");

        // Probes. Every client that exists before this is a person's, and every token a node's.
        EnsureColumn(connection, "clients", "kind", "TEXT NOT NULL DEFAULT 'user'");
        EnsureColumn(connection, "enrollment_tokens", "kind", "TEXT NOT NULL DEFAULT 'node'");
        EnsureColumn(connection, "probe_results", "handshake", "INTEGER NULL");

        // Grouping peers by the person who holds them. Filed nowhere until an operator says so,
        // so NULL is the honest start here too.
        EnsureColumn(connection, "clients", "group_id", "TEXT NULL");

        // The arrangement a panel that predates grouping never had. Defaulting every row to 0
        // would order the list by created_at alone and silently reshuffle what an operator is
        // used to seeing, so the existing order - by tunnel address, which is what the peer list
        // has always shown - is written out once.
        if (EnsureColumn(connection, "clients", "sort_order", "INTEGER NOT NULL DEFAULT 0"))
        {
            SeedSortOrderFromAddresses(connection);
        }

        // Indexed here rather than in the schema above: a clients table written before grouping
        // has no group_id while that runs, and an index on a column that does not exist yet is
        // an error that stops the panel from opening an older database at all.
        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_clients_group ON clients (group_id)";
            index.ExecuteNonQuery();
        }

        if (!OperatingSystem.IsWindows() && File.Exists(options.DatabasePath))
        {
            // The database holds the fleet private key: never group- or world-readable.
            File.SetUnixFileMode(options.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogInformation("Control plane database ready at {Path}.", options.DatabasePath);
    }

    /// <returns>True when the column was missing and has just been added.</returns>
    private static bool EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (columns.ExecuteScalar() is not null)
        {
            return false;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// Numbers the existing peers by tunnel address, once, so an upgraded panel opens on the list
    /// the operator already knows. SQLite cannot sort dotted quads numerically, so the ordering is
    /// worked out here and only the resulting positions are written.
    /// </summary>
    private static void SeedSortOrderFromAddresses(SqliteConnection connection)
    {
        var ids = new List<(string Id, long Order)>();
        using (var read = connection.Sql("SELECT id, address FROM clients"))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add((reader.GetString("id"), AddressOrder(reader.GetString("address"))));
            }
        }

        using var transaction = connection.BeginTransaction();
        var position = 0;
        foreach (var (id, _) in ids.OrderBy(entry => entry.Order))
        {
            using var update = connection.Sql(
                "UPDATE clients SET sort_order = $order WHERE id = $id",
                ("$order", position++),
                ("$id", id));

            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Sort key for "10.8.0.12/32", so .12 lands after .9 rather than after .1.</summary>
    private static long AddressOrder(string address)
        => address.Split('/')[0]
            .Split('.')
            .Aggregate(0L, (total, octet) => (total * 256) + (long.TryParse(octet, out var value) ? value : 0));

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
            failover_mode        TEXT    NOT NULL DEFAULT 'manual',
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
            kind           TEXT    NOT NULL DEFAULT 'user',
            group_id       TEXT    NULL,
            sort_order     INTEGER NOT NULL DEFAULT 0,
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_name ON clients (name COLLATE NOCASE);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_address ON clients (address);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_clients_public_key ON clients (public_key);

        -- How the operator files peers: one group per person, the peers in it their devices.
        -- Purely the panel's own bookkeeping - no node is ever told about it, and no client
        -- config changes when a peer is filed somewhere else. It lives here rather than in a
        -- browser so every device the operator signs in from reads the same arrangement.
        -- No foreign key on clients.group_id, like everywhere else here: deleting a group clears
        -- the column in the same transaction, which keeps the "a group never deletes a peer"
        -- rule in code that can be read.
        CREATE TABLE IF NOT EXISTS client_groups (
            id         TEXT PRIMARY KEY,
            name       TEXT    NOT NULL COLLATE NOCASE,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_at TEXT    NOT NULL,
            updated_at TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_client_groups_name ON client_groups (name COLLATE NOCASE);

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
            enrolled_at       TEXT    NOT NULL,
            last_up_at        TEXT    NULL,
            failover_priority INTEGER NOT NULL DEFAULT 100,
            auto_failover     INTEGER NOT NULL DEFAULT 1
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nodes_agent_key ON nodes (agent_public_key);

        CREATE TABLE IF NOT EXISTS enrollment_tokens (
            token_hash      TEXT PRIMARY KEY,
            node_name       TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            expires_at      TEXT NOT NULL,
            used_at         TEXT NULL,
            used_by_node_id TEXT NULL,
            kind            TEXT NOT NULL DEFAULT 'node'
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

        -- What each peer moved, hour by hour: the difference between two status reports, credited
        -- to the hour the later one arrived in. peer_stats answers how much a peer has ever moved;
        -- this answers when, which is the only way to say anything about a person's week.
        --
        -- Keyed by the client, not by public key: this is the panel's bookkeeping about a person,
        -- and it is deleted with them. The node is deliberately not part of the key - a peer that
        -- moves between nodes mid-hour is still one person's hour, and splitting by node would
        -- multiply every row by the size of the fleet to answer a question nobody asks here.
        CREATE TABLE IF NOT EXISTS client_usage (
            client_id         TEXT    NOT NULL,
            bucket_start      TEXT    NOT NULL,
            received_bytes    INTEGER NOT NULL,
            transmitted_bytes INTEGER NOT NULL,
            updated_at        TEXT    NOT NULL,
            PRIMARY KEY (client_id, bucket_start)
        );
        -- Retention prunes by bucket, and every read is a range over it.
        CREATE INDEX IF NOT EXISTS ix_client_usage_bucket ON client_usage (bucket_start);

        CREATE TABLE IF NOT EXISTS events (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            at       TEXT NOT NULL,
            kind     TEXT NOT NULL,
            actor    TEXT NULL,
            node_id  TEXT NULL,
            message  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_at ON events (at DESC);

        -- A probe is a client of the fleet placed where clients are. It authenticates like an
        -- agent, with a key it generated itself, and tunnels with the client row it is bound to.
        CREATE TABLE IF NOT EXISTS probes (
            id               TEXT PRIMARY KEY,
            name             TEXT    NOT NULL,
            hostname         TEXT    NULL,
            agent_public_key TEXT    NOT NULL,
            agent_version    TEXT    NULL,
            client_id        TEXT    NOT NULL,
            last_seen_at     TEXT    NULL,
            revoked          INTEGER NOT NULL DEFAULT 0,
            enrolled_at      TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_probes_agent_key ON probes (agent_public_key);

        -- The latest result per probe and node, not a history. last_reachable_at and
        -- failing_since survive the rows that replace them: they are what says how long a node
        -- has been failing, which a single latest result cannot.
        CREATE TABLE IF NOT EXISTS probe_results (
            probe_id          TEXT    NOT NULL,
            node_id           TEXT    NOT NULL,
            address           TEXT    NOT NULL,
            outcome           TEXT    NOT NULL,
            checked_at        TEXT    NOT NULL,
            last_reachable_at TEXT    NULL,
            failing_since     TEXT    NULL,
            handshake         INTEGER NULL,
            latency_ms        INTEGER NULL,
            detail            TEXT    NULL,
            PRIMARY KEY (probe_id, node_id)
        );
        """;
}
