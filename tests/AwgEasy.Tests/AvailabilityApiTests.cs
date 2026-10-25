using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The availability history as the panel serves it: a real round against a real panel, then the
/// page's own request for it. What matters here is that the two agree - the history is only worth
/// keeping if it is the same verdict the node cards show, taken from the same evaluator.
/// </summary>
public class AvailabilityApiTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private static async Task<AvailabilitySummaryResponse> AvailabilityAsync(HttpClient admin, string window = "24h")
        => (await admin.GetFromJsonAsync<AvailabilitySummaryResponse>($"/api/nodes/availability?window={window}"))!;

    [Fact]
    public async Task A_failover_round_records_a_sample_for_every_node()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-healthy", "203.0.113.41");

        var before = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);
        await EvaluateAsync(admin);
        var after = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);

        // One more sample than before, and it is a healthy one: the agent reports its tunnel up
        // and no probe contradicts it, which is exactly what the card says too.
        Assert.Equal(before.Samples + 1, after.Samples);
        Assert.Equal(before.Healthy + 1, after.Healthy);
        Assert.Equal(1, after.Uptime);
    }

    /// <summary>
    /// A node reporting its tunnel down is recorded as down rather than as nothing, so the hour it
    /// spent that way is still in the record once it recovers.
    /// </summary>
    [Fact]
    public async Task A_node_reporting_its_tunnel_down_is_recorded_as_down()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-down", "203.0.113.42");

        await ReportAsync(node, interfaceUp: false);
        var before = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);
        await EvaluateAsync(admin);
        var after = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);

        Assert.Equal(before.Down + 1, after.Down);
        Assert.Equal(NodeHealthStates.Down, (await NodeStateAsync(admin, node.Id)).Health);
    }

    /// <summary>
    /// The breakdown that the combined verdict cannot give: which vantage point reaches the node.
    /// This is the question an operator actually has when a node is blocked - whose network is it
    /// blocked in - and it is why the per-probe rows are kept at all.
    /// </summary>
    [Fact]
    public async Task Each_probe_round_is_recorded_against_the_probe_that_saw_it()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-probed", "203.0.113.43");
        var (client, probe) = await ProbeAsync(admin, "history-vantage");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable, handshake: true))).EnsureSuccessStatusCode();

        var seen = (await AvailabilityAsync(admin)).Probes.Single(row => row.ProbeId == probe.NodeId);
        var about = seen.Nodes.Single(row => row.NodeId == node.Id);

        Assert.Equal(1, about.Unreachable);
        Assert.Equal(0, about.Reachable);
        // The handshake completed and nothing followed it, which is the DPI signature rather than
        // a blocked address - and the number is what lets the page say which it was.
        Assert.Equal(1, about.Handshakes);
        Assert.Equal(0, about.Reachability);
    }

    [Fact]
    public async Task The_series_is_dense_so_an_unrecorded_hour_is_not_a_failing_one()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-dense", "203.0.113.44");
        await EvaluateAsync(admin);

        var summary = await AvailabilityAsync(admin);
        var series = summary.Nodes.Single(row => row.NodeId == node.Id).Series;

        Assert.Equal("hour", summary.Bucket);
        Assert.Equal(24, series.Length);
        // Every hour of the window is present, and the ones the panel recorded nothing in carry no
        // samples at all - which is what keeps them from reading as hours the node was down.
        Assert.All(series, point => Assert.True(point.Samples >= 0));
        Assert.Contains(series, point => point.Samples > 0);
        Assert.All(
            series.Where(point => point.Samples == 0),
            point => Assert.Equal(0, point.Healthy + point.Blocked + point.Down + point.Silent + point.Unknown));
    }

    [Fact]
    public async Task Every_node_appears_even_with_no_history_of_its_own()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-fresh", "203.0.113.45");

        var row = Assert.Single((await AvailabilityAsync(admin)).Nodes, entry => entry.NodeId == node.Id);

        // Present, with whatever has been recorded so far. Uptime over no samples is null rather
        // than zero: "nothing recorded" and "never once healthy" are opposite answers.
        Assert.Equal(node.Name, row.Name);
        if (row.Samples == 0)
        {
            Assert.Null(row.Uptime);
        }
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    public async Task The_windows_are_the_same_ones_the_traffic_page_offers(string window)
    {
        var admin = await AdminAsync();
        var summary = await AvailabilityAsync(admin, window);

        Assert.Equal(window, summary.Window);
        Assert.Equal(window is "24h" or "7d" ? "hour" : "day", summary.Bucket);
    }

    [Fact]
    public async Task An_unknown_window_is_refused_with_a_stable_code()
    {
        var admin = await AdminAsync();
        var response = await admin.GetAsync("/api/nodes/availability?window=forever");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Equal("availability_window_invalid", error.Code);
    }

    [Fact]
    public async Task An_unreal_timezone_offset_is_refused()
    {
        var admin = await AdminAsync();
        var response = await admin.GetAsync("/api/nodes/availability?offset=2000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("availability_offset_invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task The_history_is_behind_the_admin_session_like_everything_else_about_the_fleet()
    {
        var response = await Fixture.CreateClient().GetAsync("/api/nodes/availability");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The repair for a record that is wrong rather than merely old - the round after a rotation is
    /// recorded as a block that never happened - and it is scoped, because the node whose numbers
    /// are wrong is usually one node.
    /// </summary>
    [Fact]
    public async Task Clearing_one_nodes_history_leaves_the_rest_of_the_fleet_alone()
    {
        var admin = await AdminAsync();
        var cleared = await NodeAsync(admin, "history-cleared", "203.0.113.46");
        var kept = await NodeAsync(admin, "history-kept", "203.0.113.47");
        await EvaluateAsync(admin);

        var before = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == kept.Id);
        var response = await admin.PostAsync($"/api/nodes/{cleared.Id}/availability/reset", null);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AvailabilityResetResponse>())!;

        var after = await AvailabilityAsync(admin);
        var gone = after.Nodes.Single(row => row.NodeId == cleared.Id);

        Assert.Equal(cleared.Id, result.NodeId);
        Assert.True(result.Dropped > 0);
        // Still listed, with nothing recorded - and no uptime at all rather than nought per cent,
        // which would be a claim about a node the panel has no evidence about any more.
        Assert.Equal(0, gone.Samples);
        Assert.Null(gone.Uptime);
        Assert.Equal(before.Samples, after.Nodes.Single(row => row.NodeId == kept.Id).Samples);
    }

    /// <summary>What every probe saw of a node goes with it: the per-probe rows are the same claim
    /// from a different vantage point, and leaving them would keep the block on the page.</summary>
    [Fact]
    public async Task Clearing_a_node_also_drops_what_the_probes_saw_of_it()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-probe-cleared", "203.0.113.48");
        var (client, probe) = await ProbeAsync(admin, "history-cleared-vantage");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();
        Assert.Contains(
            (await AvailabilityAsync(admin)).Probes.Single(row => row.ProbeId == probe.NodeId).Nodes,
            about => about.NodeId == node.Id);

        (await admin.PostAsync($"/api/nodes/{node.Id}/availability/reset", null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain(
            (await AvailabilityAsync(admin)).Probes.Single(row => row.ProbeId == probe.NodeId).Nodes,
            about => about.NodeId == node.Id);
    }

    [Fact]
    public async Task Clearing_the_whole_history_leaves_every_node_listed_with_no_evidence()
    {
        var admin = await AdminAsync();
        await NodeAsync(admin, "history-fleet-clear", "203.0.113.49");
        await EvaluateAsync(admin);

        var response = await admin.PostAsync("/api/nodes/availability/reset", null);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AvailabilityResetResponse>())!;

        var after = await AvailabilityAsync(admin);

        // Null rather than a node id: this one was about the fleet.
        Assert.Null(result.NodeId);
        Assert.True(result.Dropped > 0);
        Assert.NotEmpty(after.Nodes);
        Assert.All(after.Nodes, row => Assert.Equal(0, row.Samples));
        Assert.All(after.Nodes, row => Assert.Null(row.Uptime));
        Assert.All(after.Probes, row => Assert.Empty(row.Nodes));
        // Nothing is being claimed any more, so the panel does not say it has been recording since
        // some hour it no longer holds.
        Assert.Null(after.RecordingSince);
    }

    /// <summary>
    /// The history is the panel's own bookkeeping, like grouping and traffic: clearing it changes
    /// nothing a node runs, so the revision must not move - a bump here would have every node in
    /// the fleet re-apply a bundle because an operator tidied up a page.
    /// </summary>
    [Fact]
    public async Task Clearing_the_history_does_not_bump_the_fleet_revision()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-no-bump", "203.0.113.50");
        await EvaluateAsync(admin);

        var revision = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        (await admin.PostAsync($"/api/nodes/{node.Id}/availability/reset", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/nodes/availability/reset", null)).EnsureSuccessStatusCode();

        Assert.Equal(revision, (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision);
    }

    /// <summary>Dropping a record of how the fleet stood is worth an entry in the one log that
    /// answers "what changed, when, and who did it".</summary>
    [Fact]
    public async Task Clearing_the_history_is_recorded_in_the_event_log()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "history-logged", "203.0.113.51");
        (await admin.PostAsync($"/api/nodes/{node.Id}/availability/reset", null)).EnsureSuccessStatusCode();

        var events = (await admin.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        var entry = events.First(record => record.Kind == "availability.reset");

        Assert.Equal(node.Id, entry.NodeId);
        Assert.Contains(node.Name, entry.Message);
    }

    [Fact]
    public async Task Clearing_the_history_of_a_node_that_is_gone_is_refused_with_a_stable_code()
    {
        var admin = await AdminAsync();
        var response = await admin.PostAsync("/api/nodes/does-not-exist/availability/reset", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("node_not_found", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Clearing_the_history_is_behind_the_admin_session_too()
    {
        var response = await Fixture.CreateClient().PostAsync("/api/nodes/availability/reset", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// The settling window in the history rather than in the alerts.
///
/// For one probe round after a rotation every probe is still speaking the previous wire format, so
/// no handshake completes anywhere and the fleet reads as blocked while nothing is wrong with it.
/// That minute is the panel's own doing, so it is not recorded against the nodes at all - a bucket
/// with no samples already means "no evidence", which is exactly the panel's position.
///
/// Its own fixture: a rotation is a fleet-wide act, and a class that shared a panel with the rest
/// of the availability tests would be deciding what they see.
/// </summary>
public class RotationHistoryTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private static async Task RotateAsync(HttpClient admin)
        => (await admin.PostAsJsonAsync("/api/fleet/rotate", new RotateFleetRequest())).EnsureSuccessStatusCode();

    private static async Task<AvailabilitySummaryResponse> AvailabilityAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<AvailabilitySummaryResponse>("/api/nodes/availability"))!;

    [Fact]
    public async Task A_probe_round_inside_the_window_is_left_out_of_the_history()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "settling-probed", "203.0.113.61");
        var (client, probe) = await ProbeAsync(admin, "settling-vantage");

        await RotateAsync(admin);
        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();

        var seen = (await AvailabilityAsync(admin)).Probes.Single(row => row.ProbeId == probe.NodeId);
        Assert.DoesNotContain(seen.Nodes, about => about.NodeId == node.Id);
    }

    /// <summary>
    /// The latest result is still stored, and deliberately: the page shows what the probes actually
    /// see, and the failover grace period is what keeps anything from being done about it. Only the
    /// history - the part that is still read next week - leaves the round out.
    /// </summary>
    [Fact]
    public async Task The_round_is_still_reported_as_the_current_state()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "settling-live", "203.0.113.62");
        var (client, probe) = await ProbeAsync(admin, "settling-live-vantage");

        await RotateAsync(admin);
        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();

        var reported = (await admin.GetFromJsonAsync<ProbeResponse[]>("/api/probes"))!.Single(row => row.Id == probe.NodeId);
        var about = Assert.Single(reported.Results, result => result.NodeId == node.Id);
        Assert.Equal(ProbeOutcomes.Unreachable, about.Outcome);
    }

    [Fact]
    public async Task A_failover_round_inside_the_window_records_no_sample()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "settling-sampled", "203.0.113.63");
        await EvaluateAsync(admin);

        var before = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);
        Assert.True(before.Samples > 0);

        await RotateAsync(admin);
        await EvaluateAsync(admin);

        // Not one more sample, in any column: the round judged the rotation, not the node.
        var after = (await AvailabilityAsync(admin)).Nodes.Single(row => row.NodeId == node.Id);
        Assert.Equal(before.Samples, after.Samples);
        Assert.Equal(before.Healthy, after.Healthy);
        Assert.Equal(before.Blocked, after.Blocked);
    }
}
