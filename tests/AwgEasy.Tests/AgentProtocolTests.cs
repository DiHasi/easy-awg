using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using AwgEasy.Node;

namespace AwgEasy.Tests;

/// <summary>
/// End-to-end coverage of the loop the whole design rests on: an operator issues a token, an
/// agent enrolls, pulls a signed bundle, and can turn it into an interface config.
/// </summary>
public class AgentProtocolTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<(HttpClient Admin, HttpClient Agent, TestAgent Node)> EnrolledAgentAsync(
        string nodeName,
        int bundleSchemaVersion = DesiredStateBundle.CurrentSchemaVersion)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest(nodeName, null, null, null));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(agentClient, token.Token, bundleSchemaVersion: bundleSchemaVersion);

        return (admin, agentClient, agent);
    }

    [Fact]
    public async Task An_enrolled_agent_pulls_a_bundle_it_can_verify_and_render()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-render");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("laptop", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();

        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;
        var bundle = agent.AcceptBundle(envelope);

        Assert.Equal(agent.NodeId, bundle.NodeId);
        Assert.Contains(bundle.Peers, peer => peer.PublicKey == client.PublicKey && peer.Address == client.Address);

        // The bundle must be enough on its own to produce a working interface config.
        var config = ServerConfigRenderer.Render(bundle, "ens3");
        Assert.Contains("[Interface]", config);
        // Host .1 of the subnet is the tunnel gateway every node holds.
        Assert.Contains("Address = 10.8.0.1/24", config);
        Assert.Contains($"AllowedIPs = {client.Address}/32", config);
    }

    // A node holds the fleet private key; it must never also learn who the clients are or hold
    // anything that would let someone impersonate them.
    [Fact]
    public async Task The_bundle_carries_no_client_private_keys_or_names()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-secrets");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("alice-phone", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;

        var payload = System.Text.Encoding.UTF8.GetString(Base64Url.Decode(envelope.Payload));
        Assert.DoesNotContain("alice-phone", payload);
        Assert.DoesNotContain("PRIV", payload[payload.IndexOf("peers", StringComparison.Ordinal)..]);

        var bundle = agent.AcceptBundle(envelope);
        Assert.Contains(bundle.Peers, peer => peer.PublicKey == client.PublicKey);
    }

    [Fact]
    public async Task Disabling_a_client_removes_its_peer_and_advances_the_revision()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-disable");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("tablet", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var before = agent.AcceptBundle((await Fetch(agentClient, agent))!);
        Assert.Contains(before.Peers, peer => peer.PublicKey == client.PublicKey);

        (await admin.PostAsync($"/api/clients/{client.Id}/disable", null)).EnsureSuccessStatusCode();

        var after = agent.AcceptBundle((await Fetch(agentClient, agent))!, appliedRevision: before.Revision);
        Assert.DoesNotContain(after.Peers, peer => peer.PublicKey == client.PublicKey);
        Assert.True(after.Revision > before.Revision);
    }

    [Fact]
    public async Task An_unchanged_fleet_answers_not_modified()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-etag");

        var first = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        var etag = first.Headers.ETag!.Tag;

        var request = agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        Assert.Equal(HttpStatusCode.NotModified, (await agentClient.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task An_unsigned_request_is_rejected()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-unsigned");

        var response = await agentClient.GetAsync($"/api/v1/agents/{agent.NodeId}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_signed_by_the_wrong_key_is_rejected()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-wrongkey");

        // A different agent signs, but claims to be the enrolled node.
        using var impostor = new TestAgent();
        var response = await agentClient.SendAsync(
            impostor.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired", asNodeId: agent.NodeId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_node_loses_access_on_its_very_next_request()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-revoked");

        (await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired")))
            .EnsureSuccessStatusCode();

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        var afterRevoke = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task An_enrollment_token_cannot_be_used_twice()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-once", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        using var first = new TestAgent();
        await first.EnrollAsync(agentClient, token.Token);

        using var second = new TestAgent();
        var response = await agentClient.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token.Token, "impostor", second.PublicKey, "test-agent/1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_node_cannot_fetch_the_bundle_of_another_node()
    {
        var (_, agentClient, first) = await EnrolledAgentAsync("node-a");
        var (_, _, second) = await EnrolledAgentAsync("node-b");

        // Correctly signed by the first node, but aimed at the second node's path.
        var response = await agentClient.SendAsync(first.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{second.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_status_report_updates_the_node_and_aggregates_client_traffic()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-status");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("desktop", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var report = new NodeStatusReport(
            AppliedRevision: 7,
            InterfaceUp: true,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [new PeerStatus(client.PublicKey, DateTimeOffset.UtcNow, 1024, 2048)],
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion);

        var response = await agentClient.SendAsync(agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();

        var nodes = (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!;
        var node = nodes.Single(n => n.Id == agent.NodeId);
        Assert.True(node.InterfaceUp);
        Assert.Equal("healthy", node.Status);
        Assert.Equal(7, node.AppliedRevision);

        var stats = (await admin.GetFromJsonAsync<ClientStatsResponse[]>("/api/clients/stats"))!;
        var clientStats = stats.Single(s => s.Id == client.Id);
        Assert.True(clientStats.Online);
        Assert.Equal(1024, clientStats.ReceivedBytes);
        Assert.Equal(agent.NodeId, clientStats.NodeId);
    }

    // The kernel will not zero a peer counter without tearing the peer down, so a reset is the
    // panel subtracting from what the node reports. It has to survive the node counting on.
    [Fact]
    public async Task Resetting_a_peer_counter_reports_only_the_traffic_that_follows()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-counters");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("counted", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        async Task ReportAsync(long received, long transmitted)
        {
            var report = new NodeStatusReport(
                AppliedRevision: 1,
                InterfaceUp: true,
                Backend: "Kernel module",
                AgentVersion: "test-agent/1.0",
                ReportedAt: DateTimeOffset.UtcNow,
                Peers: [new PeerStatus(client.PublicKey, DateTimeOffset.UtcNow, received, transmitted)],
                Metrics: null,
                LastError: null,
                BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion);

            var response = await agentClient.SendAsync(agent.SignedRequest(
                HttpMethod.Post,
                $"/api/v1/agents/{agent.NodeId}/status",
                JsonContent.Create(report)));

            response.EnsureSuccessStatusCode();
        }

        async Task<ClientStatsResponse> ReadAsync()
        {
            var stats = (await admin.GetFromJsonAsync<ClientStatsResponse[]>("/api/clients/stats"))!;
            return stats.Single(s => s.Id == client.Id);
        }

        await ReportAsync(5_000, 9_000);

        var reset = await admin.PostAsync($"/api/clients/{client.Id}/stats/reset", null);
        reset.EnsureSuccessStatusCode();

        var afterReset = (await reset.Content.ReadFromJsonAsync<ClientStatsResponse>())!;
        Assert.Equal(0, afterReset.ReceivedBytes);
        Assert.Equal(0, afterReset.TransmittedBytes);
        Assert.NotNull(afterReset.StatsResetAt);

        // The node knows nothing of the reset and keeps counting up from where it was.
        await ReportAsync(5_400, 9_250);

        var later = await ReadAsync();
        Assert.Equal(400, later.ReceivedBytes);
        Assert.Equal(250, later.TransmittedBytes);

        // A node that rebooted starts from zero; everything it reads now is traffic since the
        // reset, so the baseline has to stop applying rather than swallow it.
        await ReportAsync(120, 60);

        var afterReboot = await ReadAsync();
        Assert.Equal(120, afterReboot.ReceivedBytes);
        Assert.Equal(60, afterReboot.TransmittedBytes);
    }

    // Per-person statistics are a difference between two status reports, so the whole chain -
    // agent signs, panel credits the interval, admin reads it back as a series - has to hold.
    [Fact]
    public async Task Reported_counters_become_a_traffic_history_for_the_peer()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-usage");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("tracked", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        async Task ReportAsync(long received, long transmitted)
        {
            var report = new NodeStatusReport(
                AppliedRevision: 1,
                InterfaceUp: true,
                Backend: "Kernel module",
                AgentVersion: "test-agent/1.0",
                ReportedAt: DateTimeOffset.UtcNow,
                Peers: [new PeerStatus(client.PublicKey, DateTimeOffset.UtcNow, received, transmitted)],
                Metrics: null,
                LastError: null,
                BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion);

            var response = await agentClient.SendAsync(agent.SignedRequest(
                HttpMethod.Post,
                $"/api/v1/agents/{agent.NodeId}/status",
                JsonContent.Create(report)));

            response.EnsureSuccessStatusCode();
        }

        // The first report only establishes where the counter stands; the second is the first
        // interval anyone can attribute to a point in time.
        await ReportAsync(10_000, 1_000);
        await ReportAsync(10_900, 1_250);

        var usage = (await admin.GetFromJsonAsync<UsageSummaryResponse>("/api/usage?window=24h"))!;

        Assert.Equal("24h", usage.Window);
        Assert.Equal("hour", usage.Bucket);
        Assert.Equal(24, usage.Series.Length);

        var peer = usage.Clients.Single(entry => entry.Id == client.Id);
        Assert.Equal(900, peer.ReceivedBytes);
        Assert.Equal(250, peer.TransmittedBytes);
        Assert.Equal(1, peer.ActiveDays);
        Assert.NotNull(peer.LastActiveAt);

        // The same traffic, read as one peer's own series rather than the fleet's.
        var own = (await admin.GetFromJsonAsync<ClientUsageSeriesResponse>($"/api/clients/{client.Id}/usage?window=24h"))!;
        Assert.Equal(900, own.ReceivedBytes);
        Assert.Equal(900, own.Series.Sum(point => point.ReceivedBytes));
        // The hour in progress is the last bucket - a page that drew only whole hours would never
        // show what is happening now. Two, because a report a moment before the hour turns is
        // credited to the hour it arrived in and the window has already moved on.
        Assert.Contains(own.Series.TakeLast(2), point => point.ReceivedBytes == 900);

        // Deleting the peer deletes what was recorded about them, not just their config.
        (await admin.DeleteAsync($"/api/clients/{client.Id}")).EnsureSuccessStatusCode();

        var after = (await admin.GetFromJsonAsync<UsageSummaryResponse>("/api/usage?window=24h"))!;
        Assert.DoesNotContain(after.Clients, entry => entry.Id == client.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/clients/{client.Id}/usage")).StatusCode);
    }

    [Fact]
    public async Task A_window_the_panel_does_not_chart_is_refused_by_name()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.GetAsync("/api/usage?window=10y");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<AwgEasy.Contracts.ApiError>())!;
        Assert.Equal("usage_window_invalid", error.Code);
    }

    // The panel opens the traffic page on the day in front of the operator, so a request that
    // names no window has to answer the same thing rather than something wider.
    [Fact]
    public async Task A_request_that_names_no_window_is_answered_with_the_day()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var usage = (await admin.GetFromJsonAsync<UsageSummaryResponse>("/api/usage"))!;

        Assert.Equal("24h", usage.Window);
        Assert.Equal("hour", usage.Bucket);
        Assert.Equal(24, usage.Series.Length);
    }

    // A daily window is cut on the reader's midnight, which is the whole point of sending an
    // offset: the window itself has to move with it, not just the labels drawn on it.
    [Theory]
    [InlineData(180)]
    [InlineData(-300)]
    [InlineData(345)]
    public async Task A_daily_window_is_aligned_to_the_readers_midnight(int offsetMinutes)
    {
        var admin = await fixture.CreateAdminClientAsync();

        var usage = (await admin.GetFromJsonAsync<UsageSummaryResponse>($"/api/usage?window=30d&offset={offsetMinutes}"))!;

        Assert.Equal("day", usage.Bucket);
        Assert.Equal(30, usage.Series.Length);
        Assert.Equal(TimeSpan.FromDays(30), usage.To - usage.From);

        // Shifted into the reader's frame, both ends land on midnight - even off the hour.
        var shift = TimeSpan.FromMinutes(offsetMinutes);
        Assert.Equal(TimeSpan.Zero, (usage.From + shift).UtcDateTime.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, (usage.To + shift).UtcDateTime.TimeOfDay);

        // And every bucket starts one of those midnights.
        Assert.All(usage.Series, point => Assert.Equal(TimeSpan.Zero, (point.At + shift).UtcDateTime.TimeOfDay));
    }

    // The panel gap-fills a series by matching the instant each bucket starts, so a daily bucket
    // the database keys one way and the panel generates another would not fail - it would draw an
    // empty month. The two totals come from the two separate paths, so they only agree if the
    // keys do.
    [Fact]
    public async Task Traffic_survives_into_a_day_bucket_cut_on_the_readers_midnight()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-usage-tz");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("tracked-tz", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        async Task ReportAsync(long received, long transmitted)
        {
            var report = new NodeStatusReport(
                AppliedRevision: 1,
                InterfaceUp: true,
                Backend: "Kernel module",
                AgentVersion: "test-agent/1.0",
                ReportedAt: DateTimeOffset.UtcNow,
                Peers: [new PeerStatus(client.PublicKey, DateTimeOffset.UtcNow, received, transmitted)],
                Metrics: null,
                LastError: null,
                BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion);

            var response = await agentClient.SendAsync(agent.SignedRequest(
                HttpMethod.Post,
                $"/api/v1/agents/{agent.NodeId}/status",
                JsonContent.Create(report)));

            response.EnsureSuccessStatusCode();
        }

        await ReportAsync(10_000, 1_000);
        await ReportAsync(10_900, 1_250);

        // This peer's own series, not the fleet's: the fixture is shared, so what other peers
        // moved is none of this test's business.
        foreach (var offset in new[] { 0, 180, -300, 345 })
        {
            var own = (await admin.GetFromJsonAsync<ClientUsageSeriesResponse>(
                $"/api/clients/{client.Id}/usage?window=30d&offset={offset}"))!;

            Assert.Equal("day", own.Bucket);
            Assert.Equal(30, own.Series.Length);

            // Summed from the gap-filled series, so a key the panel could not match would read 0.
            Assert.Equal(900, own.ReceivedBytes);
            Assert.Equal(900, own.Series.Sum(point => point.ReceivedBytes));
            Assert.Single(own.Series, point => point.ReceivedBytes > 0);

            // And the fleet summary's row for the same peer, which is counted without gap-filling.
            var usage = (await admin.GetFromJsonAsync<UsageSummaryResponse>($"/api/usage?window=30d&offset={offset}"))!;
            Assert.Equal(900, usage.Clients.Single(entry => entry.Id == client.Id).ReceivedBytes);
        }
    }

    [Fact]
    public async Task An_offset_no_place_on_earth_has_is_refused_by_name()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.GetAsync("/api/usage?window=30d&offset=900");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<AwgEasy.Contracts.ApiError>())!;
        Assert.Equal("usage_offset_invalid", error.Code);
    }

    // An operator who revokes a node, then deletes it, must not be left with a server that can
    // never rejoin. A fresh token is the authorization to adopt it again.
    [Fact]
    public async Task A_deleted_node_can_rejoin_with_a_fresh_token()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-rejoin");
        var originalNodeId = agent.NodeId;

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/nodes/{agent.NodeId}")).EnsureSuccessStatusCode();

        var rejected = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // The same agent, same key pair, new token - exactly what re-running the installer does.
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-rejoin-again", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;
        await agent.EnrollAsync(agentClient, token.Token);

        Assert.NotEqual(originalNodeId, agent.NodeId);

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
    }

    // Revoking without deleting is a deliberate act; enrolling over it should say so rather than
    // failing on a database constraint.
    [Fact]
    public async Task Enrolling_a_server_that_is_still_registered_is_refused_with_a_reason()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-still-there");

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-duplicate", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var response = await agentClient.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token.Token, "same-server", agent.PublicKey, "test-agent/1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<AwgEasy.Contracts.ApiError>())!;
        Assert.Equal("enrollment_key_registered", error.Code);
        Assert.Contains("Delete that node", error.Message);
    }

    // A synchronized fleet upgrade means the panel runs the new schema while some nodes still run
    // the old one. Each node has to get the newest bundle it can actually apply, or upgrading the
    // panel would take every un-walked node's tunnel down.
    [Fact]
    public async Task A_node_one_schema_behind_is_served_the_downgraded_profile()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var profile = new ServerObfuscationProfile
        {
            S1 = 16,
            S2 = 16,
            S3 = 16,
            S4 = 16,
            H1 = "1000000-1000500",
            HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
            RandomTrailers = true,
            DefaultRekeyAfterTime = "110-130",
            DefaultRejectAfterTime = "170-190"
        };

        (await admin.PutAsJsonAsync("/api/fleet/obfuscation", profile)).EnsureSuccessStatusCode();

        var (_, legacyClient, legacy) = await EnrolledAgentAsync(
            "node-legacy-schema",
            DesiredStateBundle.MinimumSupportedSchemaVersion);

        var legacyBundle = legacy.AcceptBundle((await Fetch(legacyClient, legacy))!);

        Assert.Equal(DesiredStateBundle.MinimumSupportedSchemaVersion, legacyBundle.SchemaVersion);
        Assert.Null(legacyBundle.Obfuscation!.HeaderProtectionKey);
        Assert.Null(legacyBundle.Obfuscation.RandomTrailers);
        Assert.Null(legacyBundle.Obfuscation.DefaultRekeyAfterTime);
        // Collapsed to the low bound rather than dropped: that is the value the node was already
        // running before the range was widened.
        Assert.Equal("1000000", legacyBundle.Obfuscation.H1);

        var (_, currentClient, current) = await EnrolledAgentAsync("node-current-schema");
        var currentBundle = current.AcceptBundle((await Fetch(currentClient, current))!);

        Assert.Equal(DesiredStateBundle.CurrentSchemaVersion, currentBundle.SchemaVersion);
        Assert.Equal("1000000-1000500", currentBundle.Obfuscation!.H1);
        Assert.True(currentBundle.Obfuscation.RandomTrailers);
        Assert.Equal("110-130", currentBundle.Obfuscation.DefaultRekeyAfterTime);
    }

    // A node that cannot apply the current profile looks healthy and in sync from the outside, so
    // the panel has to say it is behind on its own.
    [Fact]
    public async Task The_panel_reports_which_nodes_cannot_apply_the_current_schema()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var (_, _, legacy) = await EnrolledAgentAsync(
            "node-schema-badge",
            DesiredStateBundle.MinimumSupportedSchemaVersion);
        var (_, _, current) = await EnrolledAgentAsync("node-schema-ok");

        var nodes = (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!;

        Assert.False(nodes.Single(n => n.Id == legacy.NodeId).SupportsCurrentSchema);
        Assert.True(nodes.Single(n => n.Id == current.NodeId).SupportsCurrentSchema);
    }

    private static async Task<SignedBundle?> Fetch(HttpClient client, TestAgent agent)
    {
        var response = await client.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedBundle>();
    }
}
