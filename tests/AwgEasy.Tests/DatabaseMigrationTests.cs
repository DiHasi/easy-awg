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

    /// <summary>The fleet row as a panel that predates the tunnel MTU wrote it - before DNS
    /// failover too, so the columns added since are left to the migration as well.</summary>
    private void WriteFleetWithoutTunnelMtu()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE fleet (
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
                created_at           TEXT    NOT NULL,
                updated_at           TEXT    NOT NULL
            );

            INSERT INTO fleet (id, generation, server_private_key, server_public_key,
                               signing_private_key, signing_public_key, signing_key_id,
                               subnet, listen_port, client_allowed_ips, client_dns,
                               endpoint_host, obfuscation_json, revision, created_at, updated_at)
            VALUES (1, 1, 'FLEET_PRIVATE', 'FLEET_PUBLIC', 'SIGN_PRIVATE', 'SIGN_PUBLIC', 'key-id',
                    '10.8.0.0/24', 51820, '0.0.0.0/0', NULL,
                    'vpn.example.com', NULL, 7, '2025-01-01T00:00:00.0000000+00:00',
                    '2025-01-01T00:00:00.0000000+00:00');
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// The upgrade this setting exists for: an already-deployed fleet has to land on 1280 by
    /// itself, because the panels that need it most are the ones nobody is going to go and
    /// reconfigure. Its identity has to survive that untouched - regenerating the key pair would
    /// disconnect every client the fleet has.
    /// </summary>
    [Fact]
    public void An_upgraded_panel_adopts_the_recommended_tunnel_mtu_without_touching_its_identity()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var fleet = new FleetRepository(database).Find();

        Assert.NotNull(fleet);
        Assert.Equal(FleetService.DefaultTunnelMtu, fleet.TunnelMtu);
        Assert.Equal("FLEET_PRIVATE", fleet.ServerPrivateKey);
        Assert.Equal("FLEET_PUBLIC", fleet.ServerPublicKey);
        // The revision is not bumped by the migration: no node is handed anything new until an
        // operator saves, and a bump here would restart every interface on first boot after an
        // upgrade.
        Assert.Equal(7, fleet.Revision);
    }

    /// <summary>
    /// A panel upgraded into fleet rotation has handed out exactly one generation's worth of
    /// configs, because until now there was only one. Starting those rows at zero would open the
    /// peer list claiming nobody holds a working config - and every share link already in
    /// somebody's hands would stop resolving on the upgrade, which is the opposite of what the
    /// generation check is for.
    /// </summary>
    [Fact]
    public void An_upgraded_panel_reads_its_existing_peers_as_holding_a_current_config()
    {
        WriteFleetWithoutTunnelMtu();
        WriteUngroupedClients(("first", "10.8.0.9"), ("second", "10.8.0.12"));

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var fleet = new FleetRepository(database).Find()!;
        var clients = new ClientRepository(database).List();

        Assert.Equal(2, clients.Count);
        Assert.All(clients, client => Assert.Equal(fleet.Generation, client.ConfigGeneration));
        Assert.Equal(0, new ClientRepository(database).CountStaleConfigs(fleet.Generation));
    }

    /// <summary>
    /// A panel that predates the pair opens as a lone panel, not as a broken half of one. The
    /// term starts at zero and no panel is in charge, because a fleet with one panel has nothing
    /// to fence against - the first standby enrollment is what makes it a pair, and it adopts the
    /// panel already serving rather than staging a failover that never happened.
    /// </summary>
    [Fact]
    public void A_panel_upgraded_into_the_pair_starts_as_a_lone_panel()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var pair = new PanelRepository(database).FindPairState();

        Assert.Equal(0, pair.Term);
        Assert.Null(pair.ActivePanelId);
        Assert.Equal(PanelFailoverModes.Manual, pair.FailoverMode);
        Assert.Empty(new PanelRepository(database).List());
    }

    /// <summary>
    /// The local row is created with this panel's own key pair, and it is the one place the role
    /// lives: a promotion has to survive a restart, so it cannot be the environment that decides.
    /// </summary>
    [Fact]
    public void The_local_panel_row_is_created_with_a_key_pair_of_its_own()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var store = new PanelLocalStore(database);
        var state = store.Ensure(PanelRoles.Active, peerUrl: null);

        Assert.Equal(PanelRoles.Active, state.Role);
        Assert.NotEmpty(state.PanelId);
        Assert.False(state.IsEnrolled);
        Assert.False(state.HasFleetState);

        // Stable across restarts: the identity a peer authenticates must not change because the
        // process did.
        Assert.Equal(state.PanelId, store.Ensure(PanelRoles.Standby, peerUrl: null).PanelId);
        Assert.Equal(PanelRoles.Active, store.Ensure(PanelRoles.Standby, peerUrl: null).Role);

        using var key = AwgEasy.Contracts.BundleSigning.ImportPrivateKey(state.AgentPrivateKey);
        Assert.Equal(state.AgentPublicKey, AwgEasy.Contracts.BundleSigning.ExportPublicKey(key));
    }

    /// <summary>
    /// A panel rewrites its own name on every round. Without that, a row created the day the pair
    /// was formed could never be renamed: nothing else touches the column, and a panel can neither
    /// revoke nor delete itself. In a container the fallback is the container id, so the first
    /// name is very often one nobody chose.
    /// </summary>
    [Fact]
    public void A_panel_can_rename_itself()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var panels = new PanelRepository(database);
        var now = DateTimeOffset.UtcNow;
        panels.Insert(new PanelRecord(
            "self",
            Name: "8f3c91a2b7de",
            Hostname: null,
            AgentPublicKey: "pub",
            Revoked: false,
            EnrolledAt: now,
            Role: PanelRoles.Active));

        panels.RecordSelf("self", "panel-amsterdam", PanelRoles.Active, 1, "https://a.example.com", "203.0.113.1", now, now);

        var panel = panels.Find("self")!;
        Assert.Equal("panel-amsterdam", panel.Name);
        Assert.Equal("https://a.example.com", panel.Url);
    }

    /// <summary>
    /// And only when it is actually named. A standby is named by the operator in the enroll
    /// dialog, so rewriting the column from a fallback would throw away the only name anybody
    /// chose - which is exactly what happened in the field: 'panel-secondary' became a container
    /// id on the first round after the rename was added.
    /// </summary>
    [Fact]
    public void A_panel_with_no_name_configured_keeps_the_one_it_was_given()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var panels = new PanelRepository(database);
        var now = DateTimeOffset.UtcNow;
        panels.Insert(new PanelRecord(
            "peer",
            Name: "panel-secondary",
            Hostname: null,
            AgentPublicKey: "pub",
            Revoked: false,
            EnrolledAt: now,
            Role: PanelRoles.Standby));

        panels.RecordSelf("peer", name: null, PanelRoles.Standby, 2, "https://b.example.com", null, now, now);

        var panel = panels.Find("peer")!;
        Assert.Equal("panel-secondary", panel.Name);
        Assert.Equal(2, panel.Term);
    }

    /// <summary>
    /// The panel that created the fleet never enrolls into anything, so it holds no pinned signing
    /// key - and without one it can never accept a snapshot, which means it can never become a
    /// standby. It does not need a peer to tell it the key: the key is in its own fleet row.
    /// </summary>
    [Fact]
    public void The_panel_that_created_the_fleet_can_pin_its_own_signing_key()
    {
        WriteFleetWithoutTunnelMtu();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var store = new PanelLocalStore(database);
        var state = store.Ensure(PanelRoles.Active, peerUrl: null);
        Assert.False(state.IsEnrolled);

        var fleet = new FleetRepository(database).Find()!;
        store.PinOwnSigningKey(fleet.SigningPublicKey, fleet.SigningKeyId);

        var pinned = store.Find()!;
        Assert.True(pinned.IsEnrolled);
        Assert.Equal(fleet.SigningPublicKey, pinned.PinnedSigningKey);
        Assert.Equal(fleet.SigningKeyId, pinned.PinnedSigningKeyId);

        // Never overwrites one pinned at enrollment: that is the only key that proves anything
        // about where the state came from.
        store.PinOwnSigningKey("something-else", "deadbeef");
        Assert.Equal(fleet.SigningPublicKey, store.Find()!.PinnedSigningKey);
    }

    /// <summary>A share link as a panel that predates the generation check wrote it.</summary>
    private void WriteShare(string tokenHash, string clientId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS client_shares (
                token_hash TEXT PRIMARY KEY,
                client_id  TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );

            INSERT INTO client_shares (token_hash, client_id, created_at, expires_at)
            VALUES ($hash, $clientId, $created, $expires);
            """;
        create.Parameters.AddWithValue("$hash", tokenHash);
        create.Parameters.AddWithValue("$clientId", clientId);
        create.Parameters.AddWithValue("$created", "2026-01-01T00:00:00.0000000+00:00");
        create.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddHours(12).ToString("O", CultureInfo.InvariantCulture));
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// The sharper half of the same backfill. A share link resolves only while it names the current
    /// generation, so a link already in somebody's hands has to be stamped with the generation it
    /// was in fact made for - otherwise merely restarting an upgraded panel would stop every
    /// outstanding link, which is the opposite of what that check exists to do.
    /// </summary>
    [Fact]
    public void An_upgraded_panel_keeps_resolving_the_share_links_already_handed_out()
    {
        WriteFleetWithoutTunnelMtu();
        WriteShare(ShareHash("live-token"), "some-client");

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var fleet = new FleetRepository(database).Find()!;
        var share = new ShareRepository(database).Resolve("live-token", DateTimeOffset.UtcNow, fleet.Generation);

        Assert.NotNull(share);
        Assert.Equal("some-client", share.ClientId);

        // And it stops the moment the identity it named is replaced, which is the point.
        Assert.Null(new ShareRepository(database).Resolve("live-token", DateTimeOffset.UtcNow, fleet.Generation + 1));
    }

    private static string ShareHash(string token)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// A panel brought up with a demo account and no operator. Counting the demo row as an admin
    /// would mean that adding AWG_ADMIN_USER afterwards never created the real one, leaving a
    /// panel that nobody can administer and no obvious reason why.
    /// </summary>
    [Fact]
    public void A_demo_account_is_not_somebody_who_can_administer_the_panel()
    {
        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var events = new EventLog(database);
        var accounts = new AdminAccounts(database, events, NullLogger<AdminAccounts>.Instance);

        accounts.Create("demo", "look-dont-touch", demo: true);
        Assert.False(accounts.AnyOperatorExists());
        Assert.True(accounts.IsDemo("demo"));

        accounts.Create("ops", "the-real-one");
        Assert.True(accounts.AnyOperatorExists());
        Assert.False(accounts.IsDemo("ops"));
    }

    /// <summary>The nodes table as a panel that predates per-node egress addresses wrote it.</summary>
    private void WriteNodeWithoutEgressColumns()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE nodes (
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

            INSERT INTO nodes (id, name, agent_public_key, status, public_ip, enrolled_at)
            VALUES ('ams', 'ams-1', 'AGENT_PUBLIC', 'healthy', '203.0.113.7',
                    '2025-01-01T00:00:00.0000000+00:00');
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// Nothing about an upgraded fleet's egress may change by itself. No plan means the kernel
    /// picks the source address exactly as it did before the column existed, and no peer is
    /// pinned to anything - an upgrade that started SNAT-ing anybody would move every one of
    /// those people to a different address without being asked.
    /// </summary>
    [Fact]
    public void An_upgraded_panel_pins_nobody_and_leaves_every_node_on_the_kernels_choice()
    {
        WriteFleetWithoutTunnelMtu();
        WriteUngroupedClients(("first", "10.8.0.9"));
        WriteNodeWithoutEgressColumns();

        var database = new Database(Options(), NullLogger<Database>.Instance);
        database.Migrate();

        var node = new NodeRepository(database, Options()).Find("ams");
        Assert.NotNull(node);
        Assert.Null(node.EgressPlan);
        Assert.True(NodeEgressPlan.Read(node.EgressPlan).IsEmpty);

        // And no report either, which is not the same as a node that answered and carries
        // nothing: the panel has simply not heard from an agent new enough to say.
        Assert.Null(node.EgressReport);
        Assert.Empty(ReportedEgress.Read(node.EgressReport));

        Assert.All(new ClientRepository(database).List(), client => Assert.Null(client.EgressLabel));
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
        Demo: DemoOptions.Disabled,
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
        Notifications: new NotificationOptions(null, null, null),
        Usage: new UsageOptions(UsageOptions.DefaultRetentionDays),
        Availability: new AvailabilityOptions(AvailabilityOptions.DefaultRetentionDays),
        Metrics: new MetricsOptions(null),
        Panel: TestPanelOptions.Default);

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
