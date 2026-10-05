using AwgEasy.Contracts;
using AwgEasy.Control;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwgEasy.Tests;

/// <summary>
/// The traffic history, which is a difference between two readings and therefore exactly as
/// trustworthy as the rules for taking that difference. A node reboots, an interface goes down, a
/// node is enrolled with peers already counting - each of those is a reading that is not a
/// continuation of the one before it, and reading it as one would invent traffic that never moved.
/// </summary>
public sealed class UsageHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "awg-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _database;
    private readonly NodeRepository _nodes;
    private readonly UsageRepository _usage;
    private readonly ClientRepository _clients;

    public UsageHistoryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = Options(UsageOptions.DefaultRetentionDays);
        _database = new Database(options, NullLogger<Database>.Instance);
        _database.Migrate();
        _nodes = new NodeRepository(_database, options);
        _usage = new UsageRepository(_database);
        _clients = new ClientRepository(_database);
    }

    private const string Key = "PUB-phone";

    /// <summary>A peer the panel knows, which is what makes a reported key a person's traffic.</summary>
    private ClientRecord Client(string name = "phone", string publicKey = Key, string address = "10.8.0.2/32")
    {
        var now = DateTimeOffset.UtcNow;
        var client = new ClientRecord(
            Guid.NewGuid().ToString("N"),
            name,
            address,
            "PRIV",
            publicKey,
            "PSK",
            Enabled: true,
            Obfuscation: null,
            now,
            now);

        _clients.Insert(client);
        return client;
    }

    private void Report(string nodeId, DateTimeOffset at, long rx, long tx, string publicKey = Key)
        => _nodes.ReplacePeerStats(nodeId, [new PeerStatus(publicKey, at, rx, tx)], at);

    private (long Rx, long Tx) Total(string clientId, DateTimeOffset from, DateTimeOffset to)
    {
        var totals = _usage.ByClient(from, to).GetValueOrDefault(clientId);
        return (totals.Rx, totals.Tx);
    }

    [Fact]
    public void Traffic_is_the_difference_between_two_reports_not_the_counter()
    {
        var client = Client();
        var at = new DateTimeOffset(2026, 3, 4, 10, 15, 0, TimeSpan.Zero);

        Report("node-a", at, rx: 1_000, tx: 100);
        Report("node-a", at.AddMinutes(10), rx: 1_500, tx: 150);

        // Not 1500: the first reading only says where the counter stood, and a node may have been
        // serving this peer long before the panel first heard from it.
        Assert.Equal((500L, 50L), Total(client.Id, at.AddHours(-1), at.AddHours(1)));
    }

    [Fact]
    public void A_counter_that_restarted_counts_from_zero_rather_than_backwards()
    {
        var client = Client();
        var at = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        Report("node-a", at, rx: 9_000, tx: 900);
        // The node rebooted: the device is counting again from nothing, so everything it reads now
        // is traffic since the restart. Subtracting would make it negative.
        Report("node-a", at.AddMinutes(20), rx: 300, tx: 30);

        Assert.Equal((300L, 30L), Total(client.Id, at, at.AddHours(1)));
    }

    [Fact]
    public void Traffic_lands_in_the_hour_the_reading_arrived_in()
    {
        var client = Client();
        var morning = new DateTimeOffset(2026, 3, 4, 10, 59, 0, TimeSpan.Zero);

        Report("node-a", morning, rx: 100, tx: 10);
        Report("node-a", morning.AddMinutes(2), rx: 600, tx: 60);

        var series = _usage.Series(
            new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
            UsageGrain.Hour,
            client.Id);

        var bucket = Assert.Single(series);
        Assert.Equal(11, bucket.At.Hour);
        Assert.Equal(500, bucket.Rx);
    }

    [Fact]
    public void Traffic_through_two_nodes_in_one_hour_is_one_persons_hour()
    {
        var client = Client();
        var at = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        // The same peer, counted independently by two nodes - which is what a failover mid-hour
        // looks like from here.
        Report("node-a", at, rx: 1_000, tx: 0);
        Report("node-b", at, rx: 50, tx: 0);
        Report("node-a", at.AddMinutes(5), rx: 1_400, tx: 0);
        Report("node-b", at.AddMinutes(5), rx: 250, tx: 0);

        Assert.Equal((600L, 0L), Total(client.Id, at, at.AddHours(1)));
    }

    [Fact]
    public void A_key_the_panel_does_not_know_is_not_recorded()
    {
        var at = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        // A node still carrying a peer for a client that was deleted, or a probe's own peer.
        Report("node-a", at, rx: 1_000, tx: 100, publicKey: "PUB-stranger");
        Report("node-a", at.AddMinutes(5), rx: 5_000, tx: 500, publicKey: "PUB-stranger");

        Assert.Empty(_usage.ByClient(at, at.AddHours(1)));
    }

    [Fact]
    public void An_idle_peer_writes_no_rows_at_all()
    {
        var client = Client();
        var at = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        Report("node-a", at, rx: 1_000, tx: 100);
        for (var tick = 1; tick <= 5; tick++)
        {
            Report("node-a", at.AddMinutes(tick), rx: 1_000, tx: 100);
        }

        // Every node carries every peer, so a fleet writing a zero per peer per report would fill
        // the table with rows that say nothing happened.
        Assert.Empty(_usage.ByClient(at, at.AddHours(1)));
    }

    [Fact]
    public void Days_are_folded_out_of_the_hours()
    {
        var client = Client();
        var first = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        Report("node-a", first, rx: 0, tx: 0);
        Report("node-a", first.AddMinutes(1), rx: 100, tx: 10);
        Report("node-a", first.AddHours(3), rx: 400, tx: 40);
        Report("node-a", first.AddDays(1), rx: 1_400, tx: 140);

        var days = _usage.Series(
            new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero),
            UsageGrain.Day,
            client.Id);

        Assert.Equal(2, days.Count);
        Assert.Equal(400, days[0].Rx);
        Assert.Equal(1_000, days[1].Rx);

        var totals = _usage.ByClient(first, first.AddDays(2))[client.Id];
        Assert.Equal(1_400, totals.Rx);
        Assert.Equal(2, totals.ActiveDays);
    }

    /// <summary>
    /// A day is the reader's day, not UTC's. Three hours of one UTC day straddle two days for a
    /// reader three hours east of it, and the page they read is about their own evenings.
    /// </summary>
    [Fact]
    public void Days_are_folded_in_the_readers_timezone()
    {
        var client = Client();
        var day = new DateTimeOffset(2026, 3, 4, 18, 0, 0, TimeSpan.Zero);

        Report("node-a", day, rx: 0, tx: 0);
        Report("node-a", day.AddMinutes(1), rx: 100, tx: 10);
        Report("node-a", day.AddHours(3).AddMinutes(1), rx: 300, tx: 30);
        Report("node-a", day.AddHours(5).AddMinutes(1), rx: 600, tx: 60);

        // In UTC all three hours are the fourth, so the whole 600 is one day.
        var utcDays = _usage.Series(
            new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
            UsageGrain.Day,
            client.Id);

        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero), Assert.Single(utcDays).At);
        Assert.Equal(600, utcDays[0].Rx);

        // Three hours east, 21:00 and 23:00 have already become the fifth: 100 on one day, 500 on
        // the next. The window is two of that reader's days, so it starts at 21:00 the day before.
        var from = new DateTimeOffset(2026, 3, 3, 21, 0, 0, TimeSpan.Zero);
        var localDays = _usage.Series(from, from.AddDays(2), UsageGrain.Day, client.Id, offsetMinutes: 180);

        Assert.Equal(2, localDays.Count);
        Assert.Equal(from, localDays[0].At);
        Assert.Equal(100, localDays[0].Rx);
        Assert.Equal(from.AddDays(1), localDays[1].At);
        Assert.Equal(500, localDays[1].Rx);

        // And the days a peer was active are counted in the same frame, or the table beside the
        // chart would disagree with it.
        Assert.Equal(1, _usage.ByClient(from, from.AddDays(2))[client.Id].ActiveDays);
        Assert.Equal(2, _usage.ByClient(from, from.AddDays(2), offsetMinutes: 180)[client.Id].ActiveDays);
    }

    /// <summary>
    /// The boundary moves both ways, and not only by whole hours: Kathmandu and the Chathams are
    /// offset by three quarters of one, and a bucket read back has to land on their midnight.
    /// </summary>
    [Theory]
    [InlineData(-300, 2026, 3, 3, 5, 0)]
    [InlineData(345, 2026, 3, 3, 18, 15)]
    public void A_day_starts_at_the_readers_own_midnight(int offsetMinutes, int year, int month, int day, int hour, int minute)
    {
        var client = Client();
        var at = new DateTimeOffset(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

        Report("node-a", at, rx: 0, tx: 0);
        Report("node-a", at.AddMinutes(1), rx: 100, tx: 10);

        var shift = TimeSpan.FromMinutes(offsetMinutes);
        var days = _usage.Series(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) - shift,
            new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero) - shift,
            UsageGrain.Day,
            client.Id,
            offsetMinutes);

        var bucket = Assert.Single(days);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero), bucket.At);
        Assert.Equal(100, bucket.Rx);
    }

    /// <summary>
    /// An hourly series is offered no opinion about timezones. The rows sit on UTC hours, so there
    /// is no finer boundary to re-cut them on and the instant is already right - a reader offset by
    /// :45 is shown an hour that genuinely begins at quarter to, which is the honest answer.
    /// </summary>
    [Fact]
    public void An_hourly_series_ignores_the_offset()
    {
        var client = Client();
        var day = new DateTimeOffset(2026, 3, 4, 18, 0, 0, TimeSpan.Zero);

        Report("node-a", day, rx: 0, tx: 0);
        Report("node-a", day.AddMinutes(1), rx: 100, tx: 10);
        Report("node-a", day.AddHours(3).AddMinutes(1), rx: 300, tx: 30);

        var from = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var hours = _usage.Series(from, from.AddDays(1), UsageGrain.Hour, client.Id, offsetMinutes: 345);

        Assert.Equal(
            [day, day.AddHours(3)],
            hours.Select(bucket => bucket.At));
    }

    [Fact]
    public void Retention_drops_what_is_older_than_the_window_and_keeps_the_rest()
    {
        var client = Client();
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        var recent = DateTimeOffset.UtcNow.AddHours(-2);

        // Two nodes, so the old pair and the recent pair are separate runs of counters rather
        // than one node that was silent for a month - which would credit the whole gap to the
        // hour it came back in, correctly but unhelpfully for what this is checking.
        Report("node-old", old, rx: 0, tx: 0);
        Report("node-old", old.AddMinutes(1), rx: 1_000, tx: 100);
        Report("node-new", recent, rx: 2_000, tx: 200);
        Report("node-new", recent.AddMinutes(1), rx: 2_500, tx: 250);

        _usage.Prune(DateTimeOffset.UtcNow.AddDays(-30));

        var kept = _usage.ByClient(DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddHours(1))[client.Id];
        Assert.Equal(500, kept.Rx);
    }

    [Fact]
    public void A_peer_that_is_deleted_takes_its_history_with_it()
    {
        var client = Client();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);

        Report("node-a", at, rx: 0, tx: 0);
        Report("node-a", at.AddMinutes(1), rx: 900, tx: 90);
        Assert.NotEmpty(_usage.ByClient(at.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));

        _usage.Forget(client.Id);

        Assert.Empty(_usage.ByClient(at.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public void Nothing_is_recorded_when_retention_is_off()
    {
        var client = Client();
        var options = Options(retentionDays: 0);
        var nodes = new NodeRepository(_database, options);
        var at = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

        nodes.ReplacePeerStats("node-a", [new PeerStatus(Key, at, 100, 10)], at);
        nodes.ReplacePeerStats("node-a", [new PeerStatus(Key, at, 900, 90)], at.AddMinutes(5));

        Assert.Empty(_usage.ByClient(at, at.AddHours(1)));

        // The live counters are the nodes' own reading and keep working either way: switching the
        // history off must not blind the panel to what a peer is doing right now.
        Assert.Equal(900, nodes.AggregatePeerStats()[Key].Rx);
        Assert.Equal(client.Id, _clients.List().Single().Id);
    }

    private ControlOptions Options(int retentionDays) => new(
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
        Usage: new UsageOptions(retentionDays));

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
