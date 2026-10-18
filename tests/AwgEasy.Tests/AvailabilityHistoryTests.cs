using AwgEasy.Contracts;
using AwgEasy.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwgEasy.Tests;

/// <summary>
/// The availability history, which is a count of samples and therefore exactly as trustworthy as
/// what it counts. An hour with no samples in it is the panel having been restarted, not the fleet
/// having been down; a revoked node is a decision, not an outage; and a probe that could not run
/// its own check has said nothing about anybody. Read the numbers without those rules and a panel
/// upgrade looks like a week of downtime.
/// </summary>
public sealed class AvailabilityHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "awg-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _database;
    private readonly NodeRepository _nodes;
    private readonly ProbeRepository _probes;
    private readonly AvailabilityRepository _availability;

    public AvailabilityHistoryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = Options();
        _database = new Database(options, NullLogger<Database>.Instance);
        _database.Migrate();
        _nodes = new NodeRepository(_database, options);
        _probes = new ProbeRepository(_database, options);
        _availability = new AvailabilityRepository(_database);
    }

    private static readonly DateTimeOffset Noon = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private string Node(string name = "ams")
    {
        var now = DateTimeOffset.UtcNow;
        var node = new NodeRecord(
            Guid.NewGuid().ToString("N"),
            name,
            Hostname: null,
            EndpointHost: null,
            AgentPublicKey: "AGENT-" + name,
            AgentVersion: "1.0",
            Status: NodeStatuses.Healthy,
            AppliedRevision: 1,
            InterfaceUp: true,
            Backend: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            EgressInterface: null,
            Mtu: null,
            PublicIp: "203.0.113.7",
            LastSeenAt: now,
            LastError: null,
            Revoked: false,
            EnrolledAt: now);

        _nodes.Insert(node);
        return node.Id;
    }

    private string Probe(string name = "home")
    {
        var probe = new ProbeRecord(
            Guid.NewGuid().ToString("N"),
            name,
            Hostname: null,
            AgentPublicKey: "PROBE-" + name,
            AgentVersion: "1.0",
            ClientId: Guid.NewGuid().ToString("N"),
            LastSeenAt: null,
            Revoked: false,
            EnrolledAt: DateTimeOffset.UtcNow);

        _probes.Insert(probe);
        return probe.Id;
    }

    private static NodeAssessment Assessment(string nodeId, string state)
        => new(nodeId, state, "because a test said so", NodeHealthEvaluator.ProbeSource, null, null, 0, 1);

    private void Round(string nodeId, string state, DateTimeOffset at)
        => _availability.Accrue([Assessment(nodeId, state)], at);

    private AvailabilityBucket Total(string nodeId, DateTimeOffset from, DateTimeOffset to)
        => _availability.ByNode(from, to).GetValueOrDefault(nodeId);

    [Fact]
    public void Every_round_is_one_sample_in_the_hour_it_ran_in()
    {
        var node = Node();

        Round(node, NodeHealthStates.Healthy, Noon);
        Round(node, NodeHealthStates.Healthy, Noon.AddMinutes(30));
        Round(node, NodeHealthStates.Blocked, Noon.AddMinutes(59));
        // The next hour, which must land in a bucket of its own.
        Round(node, NodeHealthStates.Healthy, Noon.AddHours(1));

        var hour = Total(node, Noon, Noon.AddHours(1));
        Assert.Equal(3, hour.Samples);
        Assert.Equal(2, hour.Healthy);
        Assert.Equal(1, hour.Blocked);

        var both = Total(node, Noon, Noon.AddHours(2));
        Assert.Equal(4, both.Samples);
        Assert.Equal(3, both.Healthy);
    }

    /// <summary>
    /// Uptime is a share of the samples taken, so the samples counter has to move with the state
    /// counters. If it ever fell behind, a node would read as more than 100% healthy.
    /// </summary>
    [Fact]
    public void The_state_counters_always_add_up_to_the_samples()
    {
        var node = Node();

        foreach (var state in (string[])
            [
                NodeHealthStates.Healthy,
                NodeHealthStates.Blocked,
                NodeHealthStates.Down,
                NodeHealthStates.Silent,
                NodeHealthStates.Unknown
            ])
        {
            Round(node, state, Noon);
        }

        var totals = Total(node, Noon, Noon.AddHours(1));
        Assert.Equal(5, totals.Samples);
        Assert.Equal(
            totals.Samples,
            totals.Healthy + totals.Blocked + totals.Down + totals.Silent + totals.Unknown);
    }

    /// <summary>
    /// A node nothing vouches for yet is honestly not healthy, so it is counted. A revoked node is
    /// not an outage at all - the panel cut it off on purpose - and recording it as unreachable
    /// would be the panel measuring its own decision and then reporting it as a fleet problem.
    /// </summary>
    [Fact]
    public void Unknown_is_recorded_and_revoked_is_not()
    {
        var node = Node();

        Round(node, NodeHealthStates.Unknown, Noon);
        Round(node, NodeHealthStates.Revoked, Noon);

        var totals = Total(node, Noon, Noon.AddHours(1));
        Assert.Equal(1, totals.Samples);
        Assert.Equal(1, totals.Unknown);
    }

    /// <summary>A round about a node this panel does not know is dropped rather than stored under
    /// an id nothing can resolve - the same rule as a probe result about one.</summary>
    [Fact]
    public void A_round_about_an_unknown_node_is_not_recorded()
    {
        _availability.Accrue([Assessment("ghost-node", NodeHealthStates.Healthy)], Noon);

        Assert.Empty(_availability.ByNode(Noon, Noon.AddHours(1)));
    }

    [Fact]
    public void A_dense_series_tells_an_empty_hour_from_a_failing_one()
    {
        var node = Node();

        Round(node, NodeHealthStates.Healthy, Noon);
        // Nothing at all in the hour after it: the panel was not running.
        Round(node, NodeHealthStates.Down, Noon.AddHours(2));

        var series = _availability.Series(Noon, Noon.AddHours(3), HistoryGrain.Hour)[node];

        // Only the hours that were recorded come back; the hole is the caller's to fill, and the
        // API fills it with a zero-sample point rather than with a healthy or a failing one.
        Assert.Equal(2, series.Count);
        Assert.Equal(Noon, series[0].At);
        Assert.Equal(1, series[0].Healthy);
        Assert.Equal(Noon.AddHours(2), series[1].At);
        Assert.Equal(1, series[1].Down);
    }

    /// <summary>
    /// A day is cut in the reader's own frame, exactly as the traffic history cuts it: "was the
    /// node up yesterday" is a question about the day they lived through, not about UTC's.
    /// </summary>
    [Fact]
    public void A_daily_series_is_bucketed_in_the_readers_own_days()
    {
        var node = Node();
        var lateEvening = new DateTimeOffset(2026, 3, 2, 22, 0, 0, TimeSpan.Zero);

        Round(node, NodeHealthStates.Healthy, lateEvening);

        var from = new DateTimeOffset(2026, 3, 1, 21, 0, 0, TimeSpan.Zero);
        var utcDays = _availability.Series(from, from.AddDays(3), HistoryGrain.Day)[node];
        var aheadByThree = _availability.Series(from, from.AddDays(3), HistoryGrain.Day, offsetMinutes: 180)[node];

        // 22:00 UTC is the 2nd in UTC and already the 3rd three hours east, so the same sample
        // lands in a different day - and each day starts at the midnight of the frame it was cut in.
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), Assert.Single(utcDays).At);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 21, 0, 0, TimeSpan.Zero), Assert.Single(aheadByThree).At);
    }

    [Fact]
    public void A_probe_round_is_counted_from_the_vantage_point_that_reported_it()
    {
        var node = Node();
        var home = Probe();
        var office = Probe("office");

        _probes.RecordRound(home, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon);
        _probes.RecordRound(office, "1.0", [Result(node, ProbeOutcomes.Unreachable, handshake: true)], Noon);
        _probes.RecordRound(office, "1.0", [Result(node, ProbeOutcomes.Unreachable, handshake: false)], Noon.AddMinutes(1));

        var seen = _availability.ByProbe(Noon, Noon.AddHours(1));

        var fromHome = seen.Single(row => row.ProbeId == home);
        Assert.Equal(1, fromHome.Reachable);
        Assert.Equal(0, fromHome.Unreachable);

        // The same node, blocked from the other network - and the handshake count separates the
        // round where the handshake went through and nothing followed from the one where it did not.
        var fromOffice = seen.Single(row => row.ProbeId == office);
        Assert.Equal(0, fromOffice.Reachable);
        Assert.Equal(2, fromOffice.Unreachable);
        Assert.Equal(1, fromOffice.Handshakes);
    }

    /// <summary>
    /// A probe that could not run its check is counted apart from one that ran and got nothing:
    /// reachability is taken over the rounds that actually happened, so a broken probe must not
    /// drag a node's numbers down any more than it may move a fleet's traffic.
    /// </summary>
    [Fact]
    public void A_probe_error_is_counted_apart_from_an_unreachable_node()
    {
        var node = Node();
        var probe = Probe();

        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Error)], Noon);
        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon.AddMinutes(1));

        var row = Assert.Single(_availability.ByProbe(Noon, Noon.AddHours(1)));
        Assert.Equal(1, row.Errors);
        Assert.Equal(1, row.Reachable);
        Assert.Equal(0, row.Unreachable);
    }

    [Fact]
    public void A_node_that_leaves_the_fleet_takes_its_history_with_it()
    {
        var node = Node();
        var probe = Probe();

        Round(node, NodeHealthStates.Healthy, Noon);
        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon);

        _availability.ForgetNode(node);

        Assert.Empty(_availability.ByNode(Noon, Noon.AddHours(1)));
        Assert.Empty(_availability.ByProbe(Noon, Noon.AddHours(1)));
    }

    [Fact]
    public void A_deleted_probe_stops_being_a_vantage_point()
    {
        var node = Node();
        var probe = Probe();

        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon);
        _availability.ForgetProbe(probe);

        Assert.Empty(_availability.ByProbe(Noon, Noon.AddHours(1)));
    }

    /// <summary>
    /// Retention drops what is outside the window, and zero retention has to drop everything:
    /// switching recording off that only hid what was already recorded would be a switch that
    /// does not do what it says.
    /// </summary>
    [Fact]
    public void Retention_drops_what_is_older_than_the_window_and_zero_drops_all_of_it()
    {
        var node = Node();
        var probe = Probe();

        Round(node, NodeHealthStates.Healthy, Noon.AddDays(-10));
        Round(node, NodeHealthStates.Healthy, Noon);
        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon.AddDays(-10));

        Assert.True(_availability.Prune(Noon.AddDays(-5)) >= 2);
        Assert.Equal(1, Total(node, Noon.AddDays(-30), Noon.AddHours(1)).Samples);

        Assert.True(_availability.Prune(DateTimeOffset.MaxValue) >= 1);
        Assert.Empty(_availability.ByNode(Noon.AddDays(-30), Noon.AddHours(1)));
    }

    /// <summary>
    /// The panel says how far back its own record goes rather than drawing a month it never
    /// recorded as a month the fleet spent down.
    /// </summary>
    [Fact]
    public void The_first_recorded_hour_is_the_oldest_of_either_history()
    {
        Assert.Null(_availability.FirstRecordedAt());

        var node = Node();
        var probe = Probe();

        Round(node, NodeHealthStates.Healthy, Noon);
        _probes.RecordRound(probe, "1.0", [Result(node, ProbeOutcomes.Reachable, handshake: true)], Noon.AddDays(-3));

        Assert.Equal(HistoryWindows.Bucket(Noon.AddDays(-3)), _availability.FirstRecordedAt());
    }

    private static ProbeResult Result(string nodeId, string outcome, bool? handshake = null)
        => new(nodeId, "203.0.113.7", outcome, Noon, LatencyMs: 12, Detail: null, Handshake: handshake);

    private ControlOptions Options() => new(
        DatabasePath: _path,
        DefaultSubnet: "10.8.0.0/24",
        DefaultListenPort: 51820,
        DefaultClientAllowedIps: "0.0.0.0/0",
        DefaultClientDns: null,
        DefaultEndpointHost: "vpn.example.com",
        BundleLifetime: TimeSpan.FromMinutes(15),
        BootstrapAdminUser: null,
        BootstrapAdminPassword: null,
        LegacyStateImportPath: null,
        Dns: new DnsFailoverOptions(null, 60, null, null),
        Failover: new FailoverOptions(
            CheckInterval: TimeSpan.FromSeconds(30),
            Grace: TimeSpan.FromMinutes(2),
            Cooldown: TimeSpan.FromMinutes(15),
            AgentStaleAfter: TimeSpan.FromSeconds(90),
            ProbeStaleAfter: TimeSpan.FromMinutes(5),
            ProbeInterval: TimeSpan.FromSeconds(60),
            ProbeHandshakeTimeout: TimeSpan.FromSeconds(15),
            ProbeCheckUrls: FailoverOptions.DefaultProbeCheckUrls,
            ProbeTrafficTimeout: TimeSpan.FromSeconds(8)),
        Notifications: new NotificationOptions(null, null, null),
        Usage: new UsageOptions(UsageOptions.DefaultRetentionDays),
        Availability: new AvailabilityOptions(AvailabilityOptions.DefaultRetentionDays),
        Metrics: new MetricsOptions(null));

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A lingering SQLite handle is not worth failing a test run over.
            }
        }
    }
}
