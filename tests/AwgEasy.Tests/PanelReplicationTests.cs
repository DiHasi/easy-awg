using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using Microsoft.Extensions.DependencyInjection;

namespace AwgEasy.Tests;

/// <summary>
/// Two real control planes, talking the real replication protocol over each other's in-memory
/// servers: signed requests, signed payloads, the ETag gate, the term, and the write guard.
///
/// They are hosted rather than mocked for the same reason the agent protocol is. The thing being
/// tested is whether a panel that has only ever replicated can take the fleet over and keep it
/// converging - and that is a claim about signatures, pinned keys and whole-table writes, none of
/// which a stand-in would exercise.
/// </summary>
public sealed class PanelReplicationTests
{
    /// <summary>
    /// An account's privileges are part of what the fleet runs on. A standby that replicated the
    /// demo account as a full one would hand it the whole panel the moment it was promoted - and
    /// the flag is optional on the wire, so that it actually crosses is worth asserting rather
    /// than assuming.
    /// </summary>
    [Fact]
    public async Task A_standby_replicates_the_demo_account_as_a_demo_account()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        var session = await (await pair.Standby.CreateDemoClientAsync()).GetFromJsonAsync<SessionResponse>("/api/auth/me");

        Assert.Equal(ControlPlaneFixture.DemoUser, session!.Username);
        Assert.True(session.Demo);
    }

    [Fact]
    public async Task A_standby_replicates_the_fleet_identity_and_every_peer()
    {
        await using var pair = await PanelPair.CreateAsync();

        var created = await pair.CreateClientAsync("Alice");
        await pair.SyncAsync();

        var activeFleet = await pair.ActiveAdmin.GetFromJsonAsync<FleetResponse>("/api/fleet");
        var standbyFleet = await pair.StandbyAdmin.GetFromJsonAsync<FleetResponse>("/api/fleet");

        // The identity is the whole point: a client config pins the server public key, so a panel
        // that took over with a different one would disconnect everybody at once.
        Assert.Equal(activeFleet!.ServerPublicKey, standbyFleet!.ServerPublicKey);
        Assert.Equal(activeFleet.Generation, standbyFleet.Generation);
        Assert.Equal(activeFleet.Revision, standbyFleet.Revision);
        Assert.Equal(activeFleet.Subnet, standbyFleet.Subnet);
        Assert.Equal(activeFleet.TunnelMtu, standbyFleet.TunnelMtu);

        var replicated = await pair.StandbyAdmin.GetFromJsonAsync<ClientResponse[]>("/api/clients");
        var peer = Assert.Single(replicated!, client => client.Id == created.Id);
        Assert.Equal(created.Address, peer.Address);
        Assert.Equal(created.PublicKey, peer.PublicKey);
    }

    [Fact]
    public async Task A_change_on_the_panel_in_charge_reaches_the_standby_on_the_next_pull()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        var created = await pair.CreateClientAsync("Bob");
        Assert.Empty((await pair.StandbyAdmin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!);

        await pair.SyncAsync();
        Assert.Single((await pair.StandbyAdmin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!, client => client.Id == created.Id);
    }

    /// <summary>A peer removed on the panel in charge has to be removed here too, or a promoted
    /// panel would hand the fleet back a client somebody deleted.</summary>
    [Fact]
    public async Task A_deleted_peer_is_deleted_on_the_standby_too()
    {
        await using var pair = await PanelPair.CreateAsync();

        var created = await pair.CreateClientAsync("Carol");
        await pair.SyncAsync();
        Assert.NotEmpty((await pair.StandbyAdmin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!);

        (await pair.ActiveAdmin.DeleteAsync($"/api/clients/{created.Id}")).EnsureSuccessStatusCode();
        await pair.SyncAsync();

        Assert.Empty((await pair.StandbyAdmin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!);
    }

    /// <summary>
    /// An unchanged fleet has to cost one cheap request. The ETag is taken over the snapshot with
    /// its stamps cleared, so if that ever stops being true this test fails rather than the fleet
    /// quietly transferring itself every thirty seconds.
    /// </summary>
    [Fact]
    public async Task An_unchanged_fleet_answers_the_snapshot_request_with_a_304()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        var first = await pair.FetchSnapshotAsync(etag: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!.Tag;

        var second = await pair.FetchSnapshotAsync(etag);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        // And a real change reopens the gate.
        await pair.CreateClientAsync("Dave");
        Assert.Equal(HttpStatusCode.OK, (await pair.FetchSnapshotAsync(etag)).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/clients")]
    [InlineData("POST", "/api/fleet/rotate")]
    [InlineData("POST", "/api/nodes/tokens")]
    [InlineData("PUT", "/api/fleet/mtu")]
    [InlineData("POST", "/api/failover/evaluate")]
    public async Task A_standby_refuses_every_change_to_the_fleet(string method, string path)
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        // The body never gets as far as being bound: the guard turns the request away first, which
        // is the point - a standby refuses the call rather than validating it.
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        using var response = await pair.StandbyAdmin.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("panel_standby", error!.Code);
    }

    /// <summary>
    /// Nodes are fail-static, so an agent turned away by a standby re-applies its cached bundle
    /// and keeps serving traffic. Answering it would be worse: the report would land in a database
    /// the next snapshot is about to overwrite.
    /// </summary>
    [Fact]
    public async Task A_standby_does_not_serve_agents_at_all()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        using var client = pair.Standby.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest("whatever", "a-node", "key", "test/1.0"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("panel_not_active", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// Rendering a config stamps the generation it was issued at, and a stamp written on a standby
    /// never reaches the panel in charge - which would then believe nobody holds a current config
    /// when somebody does.
    /// </summary>
    [Fact]
    public async Task A_standby_does_not_hand_out_configs()
    {
        await using var pair = await PanelPair.CreateAsync();
        var created = await pair.CreateClientAsync("Erin");
        await pair.SyncAsync();

        using var response = await pair.StandbyAdmin.GetAsync($"/api/clients/{created.Id}/config");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("panel_standby", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task A_standby_that_has_never_synced_refuses_to_be_promoted()
    {
        await using var pair = await PanelPair.CreateAsync();

        using var response = await pair.StandbyAdmin.PostAsync("/api/panel/promote", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("panel_never_synced", error!.Code);
        Assert.Contains("every node would drop every peer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promoting_the_standby_moves_the_term_and_the_panel_record()
    {
        await using var pair = await PanelPair.CreateAsync(recordName: "panel.example.com");
        await pair.SyncAsync();

        var before = await pair.StandbyAdmin.GetFromJsonAsync<PanelPairResponse>("/api/panel");
        Assert.Equal(PanelRoles.Standby, before!.Role);
        Assert.Equal(1, before.Term);

        var switched = await pair.PromoteStandbyAsync();

        Assert.Equal(2, switched.Term);
        Assert.Null(switched.Warning);
        Assert.Equal(PanelRoles.Active, switched.Pair.Role);
        Assert.Equal(switched.Pair.PanelId, switched.Pair.ActivePanelId);

        // The record the agents follow now names the panel that took over.
        var target = Assert.Single(pair.Standby.Dns.Applied);
        Assert.Equal("panel.example.com", target.Name);
        Assert.Equal("203.0.113.2", target.Address);
    }

    [Fact]
    public async Task A_promoted_panel_accepts_the_changes_it_refused_a_moment_earlier()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();
        await pair.PromoteStandbyAsync();

        var response = await pair.StandbyAdmin.PostAsJsonAsync("/api/clients", new CreateClientRequest("Frank", null));

        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
        Assert.Equal("Frank", created.Name);
    }

    /// <summary>
    /// The fencing, which is the whole reason the term exists. The panel that was in charge is
    /// still running and still believes it is; one signed hello to its peer is enough for it to
    /// find out otherwise, and it steps down on arithmetic rather than on a judgement.
    /// </summary>
    [Fact]
    public async Task The_superseded_panel_steps_down_the_next_time_it_looks()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();
        await pair.PromoteStandbyAsync();

        var stillClaiming = await pair.ActiveAdmin.GetFromJsonAsync<PanelPairResponse>("/api/panel");
        Assert.Equal(PanelRoles.Active, stillClaiming!.Role);

        var after = await pair.ActiveAdmin.PostAsync("/api/panel/sync", null);
        after.EnsureSuccessStatusCode();
        var demoted = (await after.Content.ReadFromJsonAsync<PanelPairResponse>())!;

        Assert.Equal(PanelRoles.Standby, demoted.Role);
        Assert.Equal(2, demoted.Term);

        // And it now refuses the writes it was accepting a moment ago, which is the point: an
        // operator on the wrong panel must not be able to feed changes into a fleet nobody reads.
        using var refused = await pair.ActiveAdmin.PostAsJsonAsync("/api/clients", new CreateClientRequest("Grace", null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }

    /// <summary>
    /// The panel that created the fleet never enrolled into anything, so it held no pinned signing
    /// key - and a standby without one can never accept a snapshot. That made stepping down a
    /// one-way door: the first panel of every pair could be superseded and then never replicate,
    /// never catch up and never come back. It pins its own fleet signing key instead, which is the
    /// same key the other panel signs with.
    /// </summary>
    [Fact]
    public async Task The_panel_that_created_the_fleet_can_replicate_once_it_has_stepped_down()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();
        await pair.PromoteStandbyAsync();

        // One round to find the higher term and step down.
        (await pair.ActiveAdmin.PostAsync("/api/panel/sync", null)).EnsureSuccessStatusCode();

        // And the next one replicates from the panel that superseded it.
        using var response = await pair.ActiveAdmin.PostAsync("/api/panel/sync", null);
        response.EnsureSuccessStatusCode();
        var state = (await response.Content.ReadFromJsonAsync<PanelPairResponse>())!;

        Assert.Equal(PanelRoles.Standby, state.Role);
        Assert.True(state.Enrolled);
        Assert.True(state.HasFleetState);
        Assert.Null(state.SyncError);
    }

    /// <summary>
    /// Whoever answers a hello has to be the panel that was asked. The configuration that breaks
    /// this is the one an operator reaches for first - giving a panel the moving record as its own
    /// AWG_PANEL_URL - and then the hello comes back from the wrong panel carrying a term that
    /// clears every check, writing a reassuring lie into the peer's row.
    /// </summary>
    [Fact]
    public async Task A_peer_url_that_reaches_the_wrong_panel_is_reported_rather_than_believed()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        using var key = BundleSigning.CreateKey();
        var panels = pair.Active.Services.GetRequiredService<PanelRepository>();
        panels.Insert(new PanelRecord(
            "ghost",
            Name: "panel-ghost",
            Hostname: null,
            AgentPublicKey: BundleSigning.ExportPublicKey(key),
            Revoked: false,
            EnrolledAt: DateTimeOffset.UtcNow,
            // Reaches the standby, not the panel this row names.
            Url: "http://panel-b.test"));

        using var response = await pair.ActiveAdmin.PostAsync("/api/panel/sync", null);
        response.EnsureSuccessStatusCode();
        var state = (await response.Content.ReadFromJsonAsync<PanelPairResponse>())!;

        Assert.Equal("refused", state.Action);
        Assert.Contains("is not panel-ghost", state.Message!, StringComparison.Ordinal);

        // And nothing was written into its row on the strength of somebody else's answer.
        Assert.Null(panels.Find("ghost")!.LastSeenAt);
    }

    /// <summary>
    /// What the whole feature is for. An agent pinned the panel in charge's bundle signing key at
    /// enrollment; after the other panel takes over, the bundle it serves has to verify against
    /// that same pinned key and carry the same fleet identity - otherwise the node refuses it and
    /// the fleet stops converging exactly when it must converge fastest.
    /// </summary>
    [Fact]
    public async Task An_agent_enrolled_against_one_panel_accepts_a_bundle_from_the_other()
    {
        await using var pair = await PanelPair.CreateAsync();

        var token = await pair.ActiveAdmin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-a", null, null, null));
        token.EnsureSuccessStatusCode();
        var enrollment = (await token.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        using var agent = new TestAgent();
        using var activeClient = pair.Active.CreateClient();
        await agent.EnrollAsync(activeClient, enrollment.Token);

        await pair.CreateClientAsync("Heidi");
        await pair.SyncAsync();
        await pair.PromoteStandbyAsync();

        using var standbyClient = pair.Standby.CreateClient();
        using var request = agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired");
        using var response = await standbyClient.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;

        // Verified with the key pinned against the *other* panel, and guarded exactly as the real
        // agent guards it.
        var bundle = agent.AcceptBundle(envelope);
        Assert.Single(bundle.Peers);
        Assert.Equal(
            (await pair.ActiveAdmin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.ServerPublicKey,
            bundle.Identity.ServerPublicKey);
    }

    /// <summary>
    /// The histories are replicated too, so a switch does not cost the operator the record of what
    /// their fleet has been doing. They travel on the live stream from a cursor rather than in the
    /// snapshot, because they change on a completely different clock.
    /// </summary>
    [Fact]
    public async Task The_traffic_history_reaches_the_standby()
    {
        await using var pair = await PanelPair.CreateAsync();

        var tokenResponse = await pair.ActiveAdmin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-a", null, null, null));
        tokenResponse.EnsureSuccessStatusCode();
        var enrollment = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var client = await pair.CreateClientAsync("Ivan");

        using var agent = new TestAgent();
        using var activeClient = pair.Active.CreateClient();
        await agent.EnrollAsync(activeClient, enrollment.Token);

        // Two reports: the first is the baseline a difference is taken from, the second is the
        // traffic. A peer with no previous reading is credited nothing, by design.
        await Report(activeClient, agent, client.PublicKey, 0, 0);
        await Report(activeClient, agent, client.PublicKey, 4_000, 1_000);

        await pair.SyncAsync();

        var usage = await pair.StandbyAdmin.GetFromJsonAsync<UsageSummaryResponse>("/api/usage?window=24h");

        Assert.Equal(4_000, usage!.ReceivedBytes);
        Assert.Equal(1_000, usage.TransmittedBytes);
        Assert.Single(usage.Clients, entry => entry.Id == client.Id && entry.ReceivedBytes == 4_000);
    }

    /// <summary>A token for one kind of thing must never enroll another: this one hands over the
    /// entire panel, and an install command pasted in the wrong place must not do that quietly.</summary>
    [Fact]
    public async Task A_node_token_cannot_enroll_a_panel()
    {
        await using var pair = await PanelPair.CreateAsync();

        var tokenResponse = await pair.ActiveAdmin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-a", null, null, null));
        tokenResponse.EnsureSuccessStatusCode();
        var enrollment = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        using var key = BundleSigning.CreateKey();
        using var activeClient = pair.Active.CreateClient();
        using var response = await activeClient.PostAsJsonAsync(
            "/api/v1/panels/enroll",
            new EnrollRequest(enrollment.Token, "a-panel", BundleSigning.ExportPublicKey(key), "test/1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("enrollment_wrong_kind", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Arming_automatic_promotion_needs_a_witness()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        using var response = await pair.ActiveAdmin.PutAsJsonAsync(
            "/api/panel/failover",
            new UpdatePanelFailoverRequest(PanelFailoverModes.Automatic));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("panel_witness_required", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task The_pair_can_be_armed_once_a_standby_has_a_witness()
    {
        await using var pair = await PanelPair.CreateAsync(witnessUrls: ["http://127.0.0.1:9/never-reached"]);
        await pair.SyncAsync();

        var response = await pair.ActiveAdmin.PutAsJsonAsync(
            "/api/panel/failover",
            new UpdatePanelFailoverRequest(PanelFailoverModes.Automatic));

        response.EnsureSuccessStatusCode();
        var state = (await response.Content.ReadFromJsonAsync<PanelPairResponse>())!;
        Assert.Equal(PanelFailoverModes.Automatic, state.Mode);

        // And the setting travels, because a standby that has lost contact still has to know what
        // it was told - that is the only moment it matters.
        await pair.SyncAsync();
        var replicated = await pair.StandbyAdmin.GetFromJsonAsync<PanelPairResponse>("/api/panel");
        Assert.Equal(PanelFailoverModes.Automatic, replicated!.Mode);
    }

    /// <summary>The brake has to work on the panel an operator can still reach, which is why it is
    /// the one write a standby accepts about itself.</summary>
    [Fact]
    public async Task A_standby_can_be_held_even_though_it_refuses_every_other_write()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        var response = await pair.StandbyAdmin.PutAsJsonAsync("/api/panel/hold", new PanelHoldRequest(true));

        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<PanelPairResponse>())!.Held);
    }

    [Fact]
    public async Task Demoting_the_only_panel_is_refused()
    {
        await using var fixture = new ControlPlaneFixture();
        using var admin = await fixture.CreateAdminClientAsync();

        using var response = await admin.PostAsync("/api/panel/demote", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("panel_no_peer", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// The other way to end up with no writable panel. Demoting the only panel is refused; so is
    /// demoting the one in charge while nobody else claims to be, because the result is two
    /// standbys and a fleet nothing can change.
    /// </summary>
    [Fact]
    public async Task Stepping_down_with_nobody_else_in_charge_is_refused()
    {
        await using var pair = await PanelPair.CreateAsync();
        await pair.SyncAsync();

        using var response = await pair.ActiveAdmin.PostAsync("/api/panel/demote", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("panel_no_active_peer", error!.Code);
        Assert.Contains("Promote the other panel first", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scrape has to survive a switch without being reconfigured, so a synced standby answers
    /// from its own copy. One that has replicated nothing has no fleet to describe, and says so
    /// rather than throwing.
    /// </summary>
    [Fact]
    public async Task Metrics_answer_on_a_synced_standby_and_not_on_a_blank_one()
    {
        await using var pair = await PanelPair.CreateAsync();

        using var client = pair.Standby.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ControlPlaneFixture.MetricsToken);

        using var blank = await client.GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blank.StatusCode);
        Assert.Equal("panel_never_synced", (await blank.Content.ReadFromJsonAsync<ApiError>())!.Code);

        await pair.CreateClientAsync("Judy");
        await pair.SyncAsync();

        using var synced = await client.GetAsync("/api/metrics");
        synced.EnsureSuccessStatusCode();
        Assert.Contains("awg_", await synced.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task Report(HttpClient client, TestAgent agent, string publicKey, long received, long transmitted)
    {
        using var request = agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(new NodeStatusReport(
                AppliedRevision: 0,
                InterfaceUp: true,
                Backend: "test",
                AgentVersion: "test/1.0",
                ReportedAt: DateTimeOffset.UtcNow,
                Peers: [new PeerStatus(publicKey, DateTimeOffset.UtcNow, received, transmitted)],
                Metrics: null,
                LastError: null,
                BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
                PublicIp: "203.0.113.7")));

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Two panels wired to each other's in-memory servers.
///
/// The wiring is mutual, which is what the late-bound handler is for: the standby has to be able
/// to pull from the panel in charge, and the panel in charge has to be able to ask the standby
/// which term it is at - and neither host exists when the other is constructed.
/// </summary>
internal sealed class PanelPair : IAsyncDisposable
{
    private sealed class LateHandler
    {
        public Func<HttpMessageHandler>? Factory { get; set; }

        public HttpMessageHandler Resolve()
            => Factory?.Invoke() ?? throw new InvalidOperationException("The peer panel has not been created yet.");
    }

    private PanelPair(
        ControlPlaneFixture active,
        ControlPlaneFixture standby,
        HttpClient activeAdmin,
        HttpClient standbyAdmin)
    {
        Active = active;
        Standby = standby;
        ActiveAdmin = activeAdmin;
        StandbyAdmin = standbyAdmin;
    }

    public ControlPlaneFixture Active { get; }

    public ControlPlaneFixture Standby { get; }

    public HttpClient ActiveAdmin { get; }

    public HttpClient StandbyAdmin { get; }

    public static async Task<PanelPair> CreateAsync(string? recordName = null, string[]? witnessUrls = null)
    {
        var standbyHandler = new LateHandler();
        var active = new ControlPlaneFixture(
            TestPanelOptions.Active(recordName: recordName, witnessUrls: witnessUrls),
            standbyHandler.Resolve);

        var activeAdmin = await active.CreateAdminClientAsync();

        var tokenResponse = await activeAdmin.PostAsJsonAsync("/api/panels/tokens", new CreatePanelRequest("panel-b"));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var standby = new ControlPlaneFixture(
            TestPanelOptions.Standby(token: token.Token, witnessUrls: witnessUrls) with { DnsRecordName = recordName },
            () => active.Server.CreateHandler());

        var standbyAdmin = await standby.CreateAdminClientAsync();
        standbyHandler.Factory = () => standby.Server.CreateHandler();

        return new PanelPair(active, standby, activeAdmin, standbyAdmin);
    }

    /// <summary>One replication round on the standby, run now instead of at the next tick.</summary>
    public async Task<PanelPairResponse> SyncAsync()
    {
        using var response = await StandbyAdmin.PostAsync("/api/panel/sync", null);
        response.EnsureSuccessStatusCode();
        var state = (await response.Content.ReadFromJsonAsync<PanelPairResponse>())!;

        // Asserted here rather than in every test: a round that quietly failed would otherwise
        // show up as a confusing 503 three lines later, and the reason is right here.
        Assert.True(state.HasFleetState, $"replication round did not apply a snapshot: {state.SyncError} / {state.Message}");
        return state;
    }

    public async Task<ClientResponse> CreateClientAsync(string name)
    {
        var response = await ActiveAdmin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    public async Task<PanelSwitchResponse> PromoteStandbyAsync()
    {
        using var response = await StandbyAdmin.PostAsync("/api/panel/promote", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PanelSwitchResponse>())!;
    }

    /// <summary>Fetches the snapshot as the standby's own client would, so the ETag gate is
    /// exercised through the real signed endpoint rather than around it.</summary>
    public async Task<HttpResponseMessage> FetchSnapshotAsync(string? etag)
    {
        var state = Standby.Services.GetRequiredService<PanelContext>().State;

        // Reaches the panel in charge through its own URL; the test handler sends it to the right
        // server whatever the host says.
        using var raw = new HttpClient(Active.Server.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://panel-a.test/api/v1/panels/{state.PanelId}/snapshot");
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        Sign(request, state, $"/api/v1/panels/{state.PanelId}/snapshot");
        return await raw.SendAsync(request);
    }

    private static void Sign(HttpRequestMessage message, PanelLocalState identity, string path)
    {
        var timestamp = AgentRequestSignature.Timestamp(DateTimeOffset.UtcNow);
        var nonce = AgentRequestSignature.NewNonce();
        var canonical = AgentRequestSignature.BuildCanonicalRequest(message.Method.Method, path, timestamp, nonce, []);

        using var key = BundleSigning.ImportPrivateKey(identity.AgentPrivateKey);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NodeHeader, identity.PanelId);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.TimestampHeader, timestamp);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.NonceHeader, nonce);
        message.Headers.TryAddWithoutValidation(AgentRequestSignature.SignatureHeader, AgentRequestSignature.Sign(key, canonical));
    }

    public async ValueTask DisposeAsync()
    {
        ActiveAdmin.Dispose();
        StandbyAdmin.Dispose();

        // Through IAsyncLifetime rather than the base factory, so each fixture also sweeps up its
        // throwaway database file.
        await ((IAsyncLifetime)Standby).DisposeAsync();
        await ((IAsyncLifetime)Active).DisposeAsync();
    }
}
