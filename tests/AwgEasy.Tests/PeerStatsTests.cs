using AwgEasy.Contracts;
using AwgEasy.Control;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwgEasy.Tests;

/// <summary>
/// Every node carries every peer, so a node nobody is connected through reports the whole client
/// list as never seen. What the panel shows has to be the fleet's best reading, not whichever
/// node reported last.
/// </summary>
public sealed class PeerStatsTests : IDisposable
{
    private const string Peer = "peer-public-key";

    private readonly string _path = Path.Combine(Path.GetTempPath(), "awg-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly NodeRepository _nodes;

    public PeerStatsTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = Options();
        var database = new Database(options, NullLogger<Database>.Instance);
        database.Migrate();
        _nodes = new NodeRepository(database, options);
    }

    private static PeerStatus Seen(DateTimeOffset? handshake, long rx, long tx)
        => new(Peer, handshake, rx, tx);

    [Fact]
    public void A_node_that_has_never_seen_a_peer_does_not_erase_the_node_that_has()
    {
        var handshake = DateTimeOffset.UtcNow.AddSeconds(-34);
        var now = DateTimeOffset.UtcNow;

        _nodes.ReplacePeerStats("node-active", [Seen(handshake, 12_500, 1_200)], now);
        _nodes.ReplacePeerStats("node-standby", [Seen(null, 0, 0)], now);

        // Reported last, so its rows are the ones a plain table scan reads last. Nullable >= is
        // false in both directions, which used to hand the answer to row order and flipped the
        // whole peer list to "never" between two refreshes.
        _nodes.ReplacePeerStats("node-standby", [Seen(null, 0, 0)], now.AddSeconds(5));

        var totals = _nodes.AggregatePeerStats()[Peer];

        Assert.Equal(handshake.ToUnixTimeSeconds(), totals.Handshake?.ToUnixTimeSeconds());
        Assert.Equal("node-active", totals.NodeId);
        Assert.Equal(12_500, totals.Rx);
        Assert.Equal(1_200, totals.Tx);
    }

    [Fact]
    public void A_peer_no_node_has_seen_still_reports_its_counters()
    {
        var now = DateTimeOffset.UtcNow;

        _nodes.ReplacePeerStats("node-a", [Seen(null, 41, 4)], now);
        _nodes.ReplacePeerStats("node-b", [Seen(null, 1, 0)], now);

        var totals = _nodes.AggregatePeerStats()[Peer];

        Assert.Null(totals.Handshake);
        Assert.Equal(42, totals.Rx);
        Assert.Equal(4, totals.Tx);
    }

    [Fact]
    public void A_reset_on_one_node_survives_a_node_that_was_never_reset()
    {
        var now = DateTimeOffset.UtcNow;

        _nodes.ReplacePeerStats("node-active", [Seen(now.AddMinutes(-1), 500, 50)], now);
        _nodes.ResetPeerCounters(Peer, now);
        // No baseline row of its own: it had nothing to zero when the operator pressed reset.
        _nodes.ReplacePeerStats("node-standby", [Seen(null, 0, 0)], now.AddSeconds(5));

        var totals = _nodes.AggregatePeerStats()[Peer];

        Assert.NotNull(totals.ResetAt);
        Assert.Equal(0, totals.Rx);
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
        Notifications: new NotificationOptions(null, null, null),
        Usage: new UsageOptions(UsageOptions.DefaultRetentionDays));

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
